using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IDownloadHistoryCleanupScheduler
{
    Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
