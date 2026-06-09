using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class DatabaseMaintenanceJob(IDatabaseMaintenanceScheduler task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueMaintenanceAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
