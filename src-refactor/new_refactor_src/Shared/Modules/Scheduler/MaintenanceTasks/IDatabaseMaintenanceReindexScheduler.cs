using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IDatabaseMaintenanceReindexScheduler
{
    Task QueueReindexAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
