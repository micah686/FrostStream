using NodaTime;
using Quartz;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;

namespace Scheduler.Triggers;

[DisallowConcurrentExecution]
public sealed class OrphanMetadataCleanupTriggerJob(
    IOrphanMetadataCleanupScheduler task,
    IClock clock,
    ILogger<OrphanMetadataCleanupTriggerJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var jobContext = ScheduledJobContextFactory.Create(context, clock);
        await task.QueueCleanupAsync(jobContext, context.CancellationToken);
        logger.LogInformation("Published orphan metadata cleanup request for schedule {ScheduleKey} due {DueWindow}.", jobContext.ScheduleKey, jobContext.DueWindowUtc);
    }
}
