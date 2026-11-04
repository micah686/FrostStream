using FrostStream.ApplicationContracts;
using NodaTime;
using Scheduler.ChannelTasks;
using Scheduler.Databridge;
using Scheduler.MaintenanceTasks;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Scheduler;

public sealed class SchedulerDispatchTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 3, 2, 0);
    private static readonly ScheduledJobContext Context = new("nightly", TaskTypeRegistry.DatabaseMaintenance,
        Now, "idem-nightly", 30, true);

    [Test]
    public async Task Dispatchers_Publish_The_Correct_Request_And_Schedule_Metadata()
    {
        var publisher = new RecordingPublisher();
        var clock = new FixedClock(Now);

        await new ChannelAssetRefresher(publisher, clock).QueueAssetRefreshAsync(Context, CancellationToken.None);
        await new ChannelScanFullScheduler(publisher, clock).QueueScanFullAsync(Context, CancellationToken.None);
        await new ChannelScanRefresher(publisher, clock).QueueScanRefreshAsync(Context, CancellationToken.None);
        await new DatabaseMaintenanceScheduler(publisher, clock).QueueMaintenanceAsync(Context, CancellationToken.None);
        await new DatabaseMaintenanceReindexScheduler(publisher, clock).QueueReindexAsync(Context, CancellationToken.None);
        await new SearchReindexScheduler(publisher, clock).QueueReindexAsync(Context, CancellationToken.None);
        await new DownloadHistoryCleanupScheduler(publisher, clock).QueueCleanupAsync(Context, CancellationToken.None);
        await new ImportSessionCleanupScheduler(publisher, clock).QueueCleanupAsync(Context, CancellationToken.None);
        await new StaleEntryCleanupScheduler(publisher, clock).QueueCleanupAsync(Context, CancellationToken.None);

        publisher.Messages.Select(item => item.Subject).ShouldBe([
            BackgroundJobSubjects.ChannelAssetRefreshRequest,
            BackgroundJobSubjects.ChannelScanFullRequest,
            BackgroundJobSubjects.ChannelScanRefreshRequest,
            BackgroundJobSubjects.DatabaseMaintenanceRequest,
            BackgroundJobSubjects.DatabaseMaintenanceReindexRequest,
            BackgroundJobSubjects.SearchReindexRequest,
            BackgroundJobSubjects.DownloadHistoryCleanupRequest,
            BackgroundJobSubjects.ImportSessionCleanupRequest,
            BackgroundJobSubjects.DatabaseStaleMediaCleanupRequest
        ]);

        for (var index = 0; index < publisher.Messages.Count; index++)
        {
            var item = publisher.Messages[index];
            var request = item.Message.ShouldBeAssignableTo<ScheduledBackgroundRequest>();
            request.ScheduleKey.ShouldBe(Context.ScheduleKey);
            request.TaskType.ShouldBe(Context.TaskType);
            request.DueWindowUtc.ShouldBe(Context.DueWindowUtc);
            request.IdempotencyKey.ShouldBe(index is 4 or 5
                ? "db-maintenance:nightly:2026-08-03T02:00:00Z"
                : Context.IdempotencyKey);
            request.OccurredAt.ShouldBe(Now);
        }

        ((DownloadHistoryCleanupRequested)publisher.Messages[6].Message).RetentionDays.ShouldBe(30);
        ((DownloadHistoryCleanupRequested)publisher.Messages[6].Message).IncludeFailed.ShouldBeTrue();
        ((ImportSessionCleanupRequested)publisher.Messages[7].Message).RetentionDays.ShouldBe(30);
    }

    [Test]
    public async Task DatabridgeClient_Maps_All_Schedule_Requests_To_Their_Subjects()
    {
        var requestClient = new RecordingRequestClient();
        var client = new DatabridgeClient(requestClient);

        await client.GetScheduleAsync("nightly");
        await client.ListActiveSchedulesAsync();
        await client.ListOverdueSchedulesAsync();

        requestClient.Subjects.ShouldBe([ScheduleSubjects.Get, ScheduleSubjects.ActiveList, ScheduleSubjects.Overdue]);
        requestClient.Timeouts.ShouldAllBe(timeout => timeout == TimeSpan.FromSeconds(10));
        requestClient.Requests[0].ShouldBeOfType<ScheduleGetRequestMessage>().Key.ShouldBe("nightly");
        requestClient.Requests[1].ShouldBeOfType<ScheduleListActiveRequestMessage>();
        requestClient.Requests[2].ShouldBeOfType<ScheduleListOverdueRequestMessage>();
    }

    private sealed class RecordingPublisher : INatsMessagePublisher
    {
        public List<(string Subject, object Message)> Messages { get; } = [];

        public Task PublishAsync<T>(string subject, T message, string? messageId,
            MessageHeaders? headers = null, CancellationToken cancellationToken = default)
        {
            Messages.Add((subject, message!));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRequestClient : INatsRequestClient
    {
        public List<string> Subjects { get; } = [];
        public List<object> Requests { get; } = [];
        public List<TimeSpan> Timeouts { get; } = [];

        public Task<TResponse?> RequestAsync<TRequest, TResponse>(string subject, TRequest request,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Subjects.Add(subject);
            Requests.Add(request!);
            Timeouts.Add(timeout);
            return Task.FromResult<TResponse?>(default);
        }
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
