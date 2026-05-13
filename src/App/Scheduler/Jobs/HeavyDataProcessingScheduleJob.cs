using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class HeavyDataProcessingScheduleJob(IHeavyDataProcessingScheduler task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueProcessingAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
