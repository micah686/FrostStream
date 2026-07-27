using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class DownloadHistoryCleanupScheduler(INatsMessagePublisher publisher, IClock clock) : IDownloadHistoryCleanupScheduler
{
    public Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            BackgroundJobSubjects.DownloadHistoryCleanupRequest,
            new DownloadHistoryCleanupRequested
            {
                ScheduleKey = context.ScheduleKey,
                TaskType = context.TaskType,
                DueWindowUtc = context.DueWindowUtc,
                IdempotencyKey = context.IdempotencyKey,
                OccurredAt = clock.GetCurrentInstant(),
                // Scheduled sweeps take the default retention and never touch failed or stopped jobs:
                // discarding a job the user could still retry is only ever an explicit human action.
                RetentionDays = null,
                IncludeFailed = false
            },
            context.IdempotencyKey,
            cancellationToken: cancellationToken);
}
