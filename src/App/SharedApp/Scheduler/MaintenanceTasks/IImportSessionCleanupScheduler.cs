using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IImportSessionCleanupScheduler
{
    Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
