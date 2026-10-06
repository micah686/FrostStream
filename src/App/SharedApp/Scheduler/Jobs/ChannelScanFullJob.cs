using NodaTime;
using Quartz;
using Scheduler.ChannelTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class ChannelScanFullJob(IChannelScanFullScheduler task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueScanFullAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
