using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class StaleEntryCleanupScheduler(INatsMessagePublisher publisher, IClock clock) : IStaleEntryCleanupScheduler
{
    public Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            BackgroundJobSubjects.StaleDatabaseCleanupRequest,
            new StaleDatabaseCleanupRequested
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
