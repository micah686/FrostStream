using Conduit.NATS;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;
using Shared.Backups;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Scheduler;

public sealed class BackupSchedulerTests
{
    [Test]
    public async Task Successful_Backup_Marks_Attempt_Then_Success()
    {
        var client = new FakeBackupServiceClient(finalStatus: "completed", pollsUntilTerminal: 2);
        var bus = new RecordingMessageBus();
        var scheduler = NewScheduler(client, bus);

        await scheduler.QueueBackupAsync(Context("backup-diff"), CancellationToken.None);

        var request = client.CreatedRequest.ShouldNotBeNull();
        request.Type.ShouldBe("diff");
        request.Scheduled.ShouldBeTrue();
        request.ScheduleKey.ShouldBe("backup-diff");
        request.IdempotencyKey.ShouldBe("idem-1");
        request.Name.ShouldNotBeNull().ShouldStartWith("scheduled-backup-diff-");

        bus.Subjects.ShouldBe([ScheduleSubjects.MarkAttempt, ScheduleSubjects.MarkSuccess]);
        client.PollCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Failed_Backup_Notifies_Admin_And_Marks_Failure()
    {
        var client = new FakeBackupServiceClient(finalStatus: "failed", pollsUntilTerminal: 1);
        var bus = new RecordingMessageBus();
        var scheduler = NewScheduler(client, bus);

        await scheduler.QueueBackupAsync(Context("backup-full"), CancellationToken.None);

        bus.Subjects.ShouldBe([
            ScheduleSubjects.MarkAttempt,
            NotificationSubjects.DispatchAdmin,
            ScheduleSubjects.MarkFailure
        ]);
    }

    [Test]
    public async Task Timeout_Is_Reported_As_Failure()
    {
        var client = new FakeBackupServiceClient(finalStatus: "completed", pollsUntilTerminal: int.MaxValue);
        var bus = new RecordingMessageBus();
        var scheduler = new BackupScheduler(client, bus, SystemClock.Instance, NullLogger<BackupScheduler>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            CompletionTimeout = TimeSpan.FromMilliseconds(50)
        };

        await scheduler.QueueBackupAsync(Context("backup-full"), CancellationToken.None);

        bus.Subjects.ShouldContain(ScheduleSubjects.MarkFailure);
        bus.Subjects.ShouldNotContain(ScheduleSubjects.MarkSuccess);
    }

    private static BackupScheduler NewScheduler(IBackupServiceClient client, IMessageBus bus)
        => new(client, bus, SystemClock.Instance, NullLogger<BackupScheduler>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(5)
        };

    private static ScheduledJobContext Context(string taskType)
        => new(
            ScheduleKey: taskType,
            TaskType: taskType,
            DueWindowUtc: Instant.FromUtc(2026, 8, 3, 2, 0),
            IdempotencyKey: "idem-1",
            RetentionDays: 0,
            IncludeFailed: false);

    private sealed class FakeBackupServiceClient(string finalStatus, int pollsUntilTerminal) : IBackupServiceClient
    {
        private static readonly Guid JobId = Guid.NewGuid();

        public CreateBackupJobRequest? CreatedRequest { get; private set; }
        public int PollCount { get; private set; }

        public Task<BackupJobDto> CreateAsync(CreateBackupJobRequest request, CancellationToken cancellationToken = default)
        {
            CreatedRequest = request;
            return Task.FromResult(Job("queued"));
        }

        public Task<BackupJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            PollCount++;
            return Task.FromResult<BackupJobDto?>(Job(PollCount >= pollsUntilTerminal ? finalStatus : "running"));
        }

        public Task<IReadOnlyList<BackupJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BackupJobDto>>([]);

        public Task<BackupRepositoryDto> ListBackupsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BackupJobDto> VerifyAsync(VerifyBackupRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static BackupJobDto Job(string status)
            => new(JobId, "backup", "full", status, "scheduled", null,
                status == "failed" ? "engine exploded" : null, DateTimeOffset.UtcNow, null, []);
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<string> Subjects { get; } = [];

        public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        {
            Subjects.Add(subject);
            return Task.CompletedTask;
        }

        public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
        {
            Subjects.Add(subject);
            return Task.CompletedTask;
        }

        public Task<ISubscription> SubscribeAsync<T>(
            string subject,
            Func<IMessageContext<T>, Task> handler,
            string? queueGroup = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TResponse?> RequestAsync<TRequest, TResponse>(
            string subject,
            TRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
