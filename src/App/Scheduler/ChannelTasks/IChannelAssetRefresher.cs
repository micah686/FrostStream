using Scheduler.Scheduling;

namespace Scheduler.ChannelTasks;

public interface IChannelAssetRefresher
{
    Task QueueAssetRefreshAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
