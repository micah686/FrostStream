using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IBackupScheduler
{
    Task QueueBackupAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
