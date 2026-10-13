using NodaTime;
using Quartz;
using Scheduler.ChannelTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class ChannelScanFullJob(IChannelScanFullScheduler task, IClock clock) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        => new(task.QueueScanFullAsync(ScheduledJobContextFactory.Create(context, clock), cancellationToken));
}
