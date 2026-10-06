using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class SearchReindexJob(ISearchReindexScheduler task, IClock clock) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        => new(task.QueueReindexAsync(ScheduledJobContextFactory.Create(context, clock), cancellationToken));
}
