using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class SearchReindexJob(ISearchReindexScheduler task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueReindexAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
