using NodaTime;
using Quartz;
using Scheduler.ChannelTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class ChannelAssetRefreshJob(IChannelAssetRefresher task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueAssetRefreshAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
