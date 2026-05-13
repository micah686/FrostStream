using Scheduler.Scheduling;

namespace Scheduler.MaintenanceTasks;

public interface IHeavyDataProcessingScheduler
{
    Task QueueProcessingAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
