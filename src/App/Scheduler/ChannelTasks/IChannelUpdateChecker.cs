using Scheduler.Scheduling;

namespace Scheduler.ChannelTasks;

public interface IChannelUpdateChecker
{
    Task QueueUpdateCheckAsync(ScheduledJobContext context, CancellationToken cancellationToken);
}
