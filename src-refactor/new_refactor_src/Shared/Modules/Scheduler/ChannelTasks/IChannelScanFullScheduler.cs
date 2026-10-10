using Scheduler.Scheduling;

namespace Scheduler.ChannelTasks;

public interface IChannelScanFullScheduler
{
    Task QueueScanFullAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
