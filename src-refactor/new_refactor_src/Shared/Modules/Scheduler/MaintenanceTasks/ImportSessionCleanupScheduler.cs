using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class ImportSessionCleanupScheduler(INatsMessagePublisher publisher, IClock clock) : IImportSessionCleanupScheduler
{
    public Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            BackgroundJobSubjects.ImportSessionCleanupRequest,
            new ImportSessionCleanupRequested
            {
                ScheduleKey = context.ScheduleKey,
                TaskType = context.TaskType,
                DueWindowUtc = context.DueWindowUtc,
                IdempotencyKey = context.IdempotencyKey,
                OccurredAt = clock.GetCurrentInstant(),
                RetentionDays = context.RetentionDays
            },
            context.IdempotencyKey,
            cancellationToken: cancellationToken);
}
