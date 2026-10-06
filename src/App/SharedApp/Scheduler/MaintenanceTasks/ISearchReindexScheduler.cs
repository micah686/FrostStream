using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface ISearchReindexScheduler
{
    Task QueueReindexAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
