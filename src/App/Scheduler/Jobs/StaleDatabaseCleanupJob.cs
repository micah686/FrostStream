using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class StaleDatabaseCleanupJob(IStaleEntryCleanupScheduler task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueCleanupAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
