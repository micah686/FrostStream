using Scheduler.Scheduling;

namespace Scheduler.ChannelTasks;

public interface IChannelScanRefresher
{
    Task QueueScanRefreshAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
