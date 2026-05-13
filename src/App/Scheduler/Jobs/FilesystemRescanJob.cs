using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Jobs;

[DisallowConcurrentExecution]
public sealed class FilesystemRescanJob(IFilesystemRescanScheduler task, IClock clock) : IJob
{
    public Task Execute(IJobExecutionContext context)
        => task.QueueRescanAsync(ScheduledJobContextFactory.Create(context, clock), context.CancellationToken);
}
