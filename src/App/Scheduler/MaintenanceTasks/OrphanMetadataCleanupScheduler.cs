using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class OrphanMetadataCleanupScheduler(INatsMessagePublisher publisher, IClock clock) : IOrphanMetadataCleanupScheduler
{
    public Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            ScheduleSubjects.OrphanMetadataCleanupRequest,
            new OrphanMetadataCleanupRequested
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
