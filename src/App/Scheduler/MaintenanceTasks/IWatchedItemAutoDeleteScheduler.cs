using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IWatchedItemAutoDeleteScheduler
{
    Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
