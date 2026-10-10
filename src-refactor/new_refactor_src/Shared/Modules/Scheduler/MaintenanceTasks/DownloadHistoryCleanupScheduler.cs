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
                RetentionDays = context.RetentionDays,
                IncludeFailed = context.IncludeFailed
            },
            context.IdempotencyKey,
            cancellationToken: cancellationToken);
}
