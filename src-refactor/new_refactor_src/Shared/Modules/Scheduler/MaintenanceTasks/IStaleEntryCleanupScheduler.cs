using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IStaleEntryCleanupScheduler
{
    Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
