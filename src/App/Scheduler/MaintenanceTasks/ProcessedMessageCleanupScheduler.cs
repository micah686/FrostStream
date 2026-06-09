using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class ProcessedMessageCleanupScheduler(INatsMessagePublisher publisher, IClock clock) : IProcessedMessageCleanupScheduler
{
    public Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            BackgroundJobSubjects.ProcessedMessageCleanupRequest,
            new ProcessedMessageCleanupRequested
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
