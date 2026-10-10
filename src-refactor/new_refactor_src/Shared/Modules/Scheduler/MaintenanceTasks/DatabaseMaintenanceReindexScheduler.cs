using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class DatabaseMaintenanceReindexScheduler(INatsMessagePublisher publisher, IClock clock)
    : IDatabaseMaintenanceReindexScheduler
{
    public Task QueueReindexAsync(ScheduledJobContext context, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            BackgroundJobSubjects.DatabaseMaintenanceReindexRequest,
            BackgroundJobRequestFactory.CreateDatabaseMaintenanceReindex(
                context.ScheduleKey,
                context.TaskType,
                context.DueWindowUtc,
                clock.GetCurrentInstant()),
            context.IdempotencyKey,
            cancellationToken: cancellationToken);
}
