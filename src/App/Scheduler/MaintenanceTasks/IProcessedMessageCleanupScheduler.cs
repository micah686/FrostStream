using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IProcessedMessageCleanupScheduler
{
    Task QueueCleanupAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
