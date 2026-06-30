using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class BackupJob(IBackupScheduler task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueBackupAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
