using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IDatabaseMaintenanceScheduler
{
    Task QueueMaintenanceAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
