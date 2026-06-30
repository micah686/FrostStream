using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class BackupScheduler(INatsMessagePublisher publisher, IClock clock) : IBackupScheduler
{
    public Task QueueBackupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            BackgroundJobSubjects.BackupRequest,
            new BackupRequested
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
