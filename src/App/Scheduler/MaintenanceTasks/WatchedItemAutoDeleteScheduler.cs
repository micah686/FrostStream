using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class WatchedItemAutoDeleteScheduler(INatsMessagePublisher publisher, IClock clock) : IWatchedItemAutoDeleteScheduler
{
    public Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            BackgroundJobSubjects.WatchedItemAutoDeleteRequest,
            new WatchedItemAutoDeleteRequested
            {
                ScheduleKey = context.ScheduleKey,
                TaskType = context.TaskType,
                DueWindowUtc = context.DueWindowUtc,
                IdempotencyKey = context.IdempotencyKey,
                OccurredAt = clock.GetCurrentInstant()
            },
            context.IdempotencyKey,
            cancellationToken: cancellationToken);
}

