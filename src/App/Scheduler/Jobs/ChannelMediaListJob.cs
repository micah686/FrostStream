using NodaTime;
using Quartz;
using Scheduler.ChannelTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class ChannelMediaListJob(IChannelMediaLister task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueMediaListAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
