using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IFilesystemRescanScheduler
{
    Task QueueRescanAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
