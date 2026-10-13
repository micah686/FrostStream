using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class BackupJob(IBackupScheduler task, IClock clock) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        => new(task.QueueBackupAsync(ScheduledJobContextFactory.Create(context, clock), cancellationToken));
}
