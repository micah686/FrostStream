using DataBridge.Data;
using DataBridge.Handlers;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared;
using Shared.Entities;
using Shared.Jobs;
using Shared.Messages;
using Shouldly;
using TUnit.Core;

namespace IntegrationTests.FrostStream.Saga;

public class SagaFailureMatrixTests
{
    [Test]
    public async Task CommitSuccessThenResponseLoss_RetryCommit_RemainsIdempotent()
    {
        await using var harness = await SagaTestHarness.CreateAsync();

        var jobId = Guid.NewGuid();
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var commitRequest = new VideoCommitRequest(
            jobId,
            idempotencyKey,
            "archive",
            "youtube/test-video/vhash-a.mp4",
            "hash-a",
            "{\"title\":\"test-video\"}",
            "youtube",
            now,
            MediaType.Video,
            Quality.P720);

        var start = await harness.RequestAsync<JobStartRequest, JobStartResponse>(
            Subjects.JobStart,
            new JobStartRequest(jobId, idempotencyKey, "archive", "https://example.test/videos/1"));
        start.ShouldNotBeNull();
        start.Proceed.ShouldBeTrue();

        var progress = await harness.RequestAsync<JobProgressRequest, JobProgressResponse>(
            Subjects.JobProgress,
            new JobProgressRequest(
                jobId,
                JobStatus.UploadedPendingCommit.ToStorageValue(),
                commitRequest.StoragePath,
                commitRequest.FileHash));
        progress.ShouldNotBeNull();
        progress.Success.ShouldBeTrue();

        var firstCommit = await harness.RequestAsync<VideoCommitRequest, VideoCommitResponse>(
            Subjects.VideoCommit,
            commitRequest);
        firstCommit.ShouldNotBeNull();
        firstCommit.Success.ShouldBeTrue();

        var retryCommit = await harness.RequestAsync<VideoCommitRequest, VideoCommitResponse>(
            Subjects.VideoCommit,
            commitRequest);
        retryCommit.ShouldNotBeNull();
        retryCommit.Success.ShouldBeTrue();

        await harness.WithDbAsync(async db =>
        {
            var versions = await db.VideoVersions
                .Where(v => v.IdempotencyKey == idempotencyKey)
                .ToListAsync();
            versions.Count.ShouldBe(1);

            var tracker = await db.JobTrackers
                .Include(t => t.Job)
                .SingleAsync(t => t.JobId == jobId);

            tracker.Job.ShouldNotBeNull();
            JobStatusCodec.Parse(tracker.Job!.Status).ShouldBe(JobStatus.Completed);
            tracker.StoragePath.ShouldBe(commitRequest.StoragePath);
            tracker.FileHash.ShouldBe(commitRequest.FileHash);
            tracker.CompletedAt.ShouldNotBeNull();
        });
    }

    [Test]
    public async Task UploadSuccessCommitFailure_ThenJobFail_MarksFailedWithoutCommittedVersion()
    {
        await using var harness = await SagaTestHarness.CreateAsync();

        var jobId = Guid.NewGuid();
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";

        var start = await harness.RequestAsync<JobStartRequest, JobStartResponse>(
            Subjects.JobStart,
            new JobStartRequest(jobId, idempotencyKey, "archive", "https://example.test/videos/2"));
        start.ShouldNotBeNull();
        start.Proceed.ShouldBeTrue();

        var progress = await harness.RequestAsync<JobProgressRequest, JobProgressResponse>(
            Subjects.JobProgress,
            new JobProgressRequest(
                jobId,
                JobStatus.UploadedPendingCommit.ToStorageValue(),
                "youtube/test-video/vhash-b.mp4",
                "hash-b"));
        progress.ShouldNotBeNull();
        progress.Success.ShouldBeTrue();

        // Simulate a commit rejection after successful upload by sending invalid payload.
        var failedCommit = await harness.RequestAsync<VideoCommitRequest, VideoCommitResponse>(
            Subjects.VideoCommit,
            new VideoCommitRequest(
                jobId,
                idempotencyKey,
                "archive",
                null!,
                "hash-b",
                "{\"title\":\"test-video\"}",
                "youtube",
                DateTime.UtcNow,
                MediaType.Video,
                Quality.P720));
        failedCommit.ShouldNotBeNull();
        failedCommit.Success.ShouldBeFalse();

        var failResponse = await harness.RequestAsync<JobFailRequest, JobFailResponse>(
            Subjects.JobFail,
            new JobFailRequest(
                jobId,
                failedCommit.ErrorMessage ?? "commit failed",
                "simulated upload-success/commit-failure test case"));
        failResponse.ShouldNotBeNull();
        failResponse.Success.ShouldBeTrue();

        await harness.WithDbAsync(async db =>
        {
            var job = await db.Jobs.SingleAsync(j => j.JobId == jobId);
            JobStatusCodec.Parse(job.Status).ShouldBe(JobStatus.Failed);
            job.RetryCount.ShouldBe(1);

            var tracker = await db.JobTrackers.SingleAsync(t => t.JobId == jobId);
            tracker.RetryCount.ShouldBe(1);

            var versions = await db.VideoVersions
                .Where(v => v.IdempotencyKey == idempotencyKey)
                .ToListAsync();
            versions.Count.ShouldBe(0);
        });
    }

    [Test]
    public async Task DuplicateDeliveries_SameJobId_ReturnDuplicateDeliveryWithoutDuplicateRows()
    {
        await using var harness = await SagaTestHarness.CreateAsync();

        var jobId = Guid.NewGuid();
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";
        var request = new JobStartRequest(jobId, idempotencyKey, "archive", "https://example.test/videos/3");

        var first = await harness.RequestAsync<JobStartRequest, JobStartResponse>(Subjects.JobStart, request);
        var second = await harness.RequestAsync<JobStartRequest, JobStartResponse>(Subjects.JobStart, request);

        first.ShouldNotBeNull();
        first.Proceed.ShouldBeTrue();

        second.ShouldNotBeNull();
        second.Proceed.ShouldBeFalse();
        second.Reason.ShouldBe("DuplicateDelivery");

        await harness.WithDbAsync(async db =>
        {
            var jobs = await db.Jobs.Where(j => j.JobId == jobId).ToListAsync();
            var trackers = await db.JobTrackers.Where(t => t.JobId == jobId).ToListAsync();

            jobs.Count.ShouldBe(1);
            trackers.Count.ShouldBe(1);
        });
    }

    [Test]
    public async Task ConcurrentSameUrlSubmissions_CreatePendingLink_AndResolveOnSourceCommit()
    {
        await using var harness = await SagaTestHarness.CreateAsync();

        var sourceCandidateA = Guid.NewGuid();
        var sourceCandidateB = Guid.NewGuid();
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";

        var reqA = new JobStartRequest(sourceCandidateA, idempotencyKey, "archive", "https://example.test/videos/4");
        var reqB = new JobStartRequest(sourceCandidateB, idempotencyKey, "archive", "https://example.test/videos/4");

        var taskA = harness.RequestAsync<JobStartRequest, JobStartResponse>(Subjects.JobStart, reqA);
        var taskB = harness.RequestAsync<JobStartRequest, JobStartResponse>(Subjects.JobStart, reqB);
        await Task.WhenAll(taskA, taskB);

        var responseA = await taskA;
        var responseB = await taskB;
        responseA.ShouldNotBeNull();
        responseB.ShouldNotBeNull();

        var proceedCount = (responseA.Proceed ? 1 : 0) + (responseB.Proceed ? 1 : 0);
        proceedCount.ShouldBe(1);

        var sourceJobId = responseA.Proceed ? sourceCandidateA : sourceCandidateB;
        var pendingJobId = responseA.Proceed ? sourceCandidateB : sourceCandidateA;
        var pendingResponse = responseA.Proceed ? responseB : responseA;

        pendingResponse.Proceed.ShouldBeFalse();
        pendingResponse.Reason.ShouldBe("AlreadyExists");

        await harness.WithDbAsync(async db =>
        {
            var pendingJob = await db.Jobs.SingleAsync(j => j.JobId == pendingJobId);
            JobStatusCodec.Parse(pendingJob.Status).ShouldBe(JobStatus.PendingLink);

            var pendingLink = await db.PendingJobLinks.SingleAsync(l => l.PendingJobId == pendingJobId);
            pendingLink.SourceJobId.ShouldBe(sourceJobId);
            pendingLink.CompletedAt.ShouldBeNull();
        });

        var progress = await harness.RequestAsync<JobProgressRequest, JobProgressResponse>(
            Subjects.JobProgress,
            new JobProgressRequest(
                sourceJobId,
                JobStatus.UploadedPendingCommit.ToStorageValue(),
                "youtube/test-video/vhash-c.mp4",
                "hash-c"));
        progress.ShouldNotBeNull();
        progress.Success.ShouldBeTrue();

        var commit = await harness.RequestAsync<VideoCommitRequest, VideoCommitResponse>(
            Subjects.VideoCommit,
            new VideoCommitRequest(
                sourceJobId,
                idempotencyKey,
                "archive",
                "youtube/test-video/vhash-c.mp4",
                "hash-c",
                "{\"title\":\"test-video\"}",
                "youtube",
                DateTime.UtcNow,
                MediaType.Video,
                Quality.P720));
        commit.ShouldNotBeNull();
        commit.Success.ShouldBeTrue();

        await harness.WithDbAsync(async db =>
        {
            var pendingJob = await db.Jobs.SingleAsync(j => j.JobId == pendingJobId);
            JobStatusCodec.Parse(pendingJob.Status).ShouldBe(JobStatus.Completed);

            var pendingLink = await db.PendingJobLinks.SingleAsync(l => l.PendingJobId == pendingJobId);
            pendingLink.CompletedAt.ShouldNotBeNull();
            pendingLink.ExistingVersionId.ShouldNotBeNull();
            pendingLink.VideoId.ShouldNotBeNull();

            var versions = await db.VideoVersions
                .Where(v => v.IdempotencyKey == idempotencyKey)
                .ToListAsync();
            versions.Count.ShouldBe(1);
        });
    }
}

internal sealed class SagaTestHarness : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly string _databasePath;
    private readonly ServiceProvider _serviceProvider;
    private readonly List<IHostedService> _startedHandlers = [];

    private SagaTestHarness(string databasePath, ServiceProvider serviceProvider)
    {
        _databasePath = databasePath;
        _serviceProvider = serviceProvider;
    }

    public static async Task<SagaTestHarness> CreateAsync()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"froststream-saga-tests-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FrostStreamDbContext>(options => options.UseSqlite($"Data Source={dbFile}"));
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();

        var provider = services.BuildServiceProvider();
        var harness = new SagaTestHarness(dbFile, provider);

        await harness.InitializeAsync();
        return harness;
    }

    public async Task<TResponse?> RequestAsync<TRequest, TResponse>(string subject, TRequest request)
    {
        var bus = _serviceProvider.GetRequiredService<IMessageBus>();
        return await bus.RequestAsync<TRequest, TResponse>(subject, request, DefaultTimeout);
    }

    public async Task WithDbAsync(Func<FrostStreamDbContext, Task> action)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();
        await action(db);
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = _startedHandlers.Count - 1; i >= 0; i--)
        {
            await _startedHandlers[i].StopAsync(CancellationToken.None);
        }

        await _serviceProvider.DisposeAsync();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private async Task InitializeAsync()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await StartHandlerAsync<JobStartHandler>();
        await StartHandlerAsync<JobProgressHandler>();
        await StartHandlerAsync<VideoCommitHandler>();
        await StartHandlerAsync<JobLinkCompleteHandler>();
        await StartHandlerAsync<JobFailHandler>();
        await StartHandlerAsync<JobStatusHandler>();
    }

    private async Task StartHandlerAsync<THandler>()
        where THandler : class, IHostedService
    {
        var handler = ActivatorUtilities.CreateInstance<THandler>(_serviceProvider);
        _startedHandlers.Add(handler);
        await handler.StartAsync(CancellationToken.None);
    }
}

internal sealed class InMemoryMessageBus : IMessageBus
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<ISubscription>> _subscriptions = new(StringComparer.Ordinal);

    public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        => PublishAsync(subject, message, headers: null, cancellationToken);

    public async Task PublishAsync<T>(
        string subject,
        T message,
        MessageHeaders? headers,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = GetSubscriptions(subject);
        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await subscription.InvokeAsync(message!, subject, cancellationToken);
        }
    }

    public Task SubscribeAsync<T>(
        string subject,
        Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(handler);

        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(subject, out var list))
            {
                list = [];
                _subscriptions[subject] = list;
            }

            list.Add(new Subscription<T>(handler));
        }

        return Task.CompletedTask;
    }

    public async Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var subscription = GetSubscriptions(subject).FirstOrDefault()
                           ?? throw new InvalidOperationException(
                               $"No subscriber found for request subject '{subject}'.");

        var response = await subscription.InvokeAsync(request!, subject, timeoutCts.Token);
        if (response == null)
        {
            return default;
        }

        if (response is TResponse typedResponse)
        {
            return typedResponse;
        }

        throw new InvalidOperationException(
            $"Subscriber for '{subject}' responded with unexpected type {response.GetType().Name}.");
    }

    private IReadOnlyList<ISubscription> GetSubscriptions(string subject)
    {
        lock (_sync)
        {
            return _subscriptions.TryGetValue(subject, out var list)
                ? list.ToArray()
                : [];
        }
    }

    private interface ISubscription
    {
        Task<object?> InvokeAsync(object message, string subject, CancellationToken cancellationToken);
    }

    private sealed class Subscription<TMessage> : ISubscription
    {
        private readonly Func<IMessageContext<TMessage>, Task> _handler;

        public Subscription(Func<IMessageContext<TMessage>, Task> handler)
        {
            _handler = handler;
        }

        public async Task<object?> InvokeAsync(object message, string subject, CancellationToken cancellationToken)
        {
            if (message is not TMessage typedMessage)
            {
                throw new InvalidOperationException(
                    $"Expected message type {typeof(TMessage).Name}, received {message.GetType().Name}.");
            }

            var context = new InMemoryMessageContext<TMessage>(typedMessage, subject);
            await _handler(context);
            return context.Response;
        }
    }
}

internal sealed class InMemoryMessageContext<TMessage> : IMessageContext<TMessage>
{
    public InMemoryMessageContext(TMessage message, string subject)
    {
        Message = message;
        Subject = subject;
    }

    public TMessage Message { get; }
    public string Subject { get; }
    public MessageHeaders Headers { get; } = MessageHeaders.Empty;
    public string? ReplyTo { get; } = null;
    public object? Response { get; private set; }

    public Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Response = response;
        return Task.CompletedTask;
    }
}
