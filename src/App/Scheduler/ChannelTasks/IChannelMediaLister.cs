using Scheduler.Scheduling;

namespace Scheduler.ChannelTasks;

public interface IChannelMediaLister
{
    Task QueueMediaListAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
