using NodaTime;
using Quartz;
using Scheduler.ChannelTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class ChannelAssetRefreshJob(IChannelAssetRefresher task, IClock clock) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        => new(task.QueueAssetRefreshAsync(ScheduledJobContextFactory.Create(context, clock), cancellationToken));
}
