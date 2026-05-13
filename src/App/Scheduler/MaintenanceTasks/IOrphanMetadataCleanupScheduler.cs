using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IOrphanMetadataCleanupScheduler
{
    Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
