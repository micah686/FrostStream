using Quartz;
using Scheduler.Triggers;
using Shared.Messaging;

namespace Scheduler.Scheduling;

public interface IQuartzJobRegistrar
{
    Task RegisterAsync(IScheduler scheduler, ScheduledTaskDto task, CancellationToken cancellationToken = default);
    Task DeleteAsync(IScheduler scheduler, string scheduleKey, CancellationToken cancellationToken = default);
}

public sealed class QuartzJobRegistrar(ILogger<QuartzJobRegistrar> logger, Shared.Deployment.SystemCapabilities? capabilities = null) : IQuartzJobRegistrar
{
    public async Task RegisterAsync(IScheduler scheduler, ScheduledTaskDto task, CancellationToken cancellationToken = default)
    {
        if (task.TaskType == TaskTypeRegistry.BackupDiff && capabilities?.Backups.Differential == false)
        {
            await DeleteAsync(scheduler, task.Key, cancellationToken);
            logger.LogInformation("Skipping unsupported differential backup schedule {ScheduleKey}.", task.Key);
            return;
        }
        if (!TaskTypeRegistry.TryGetJobType(task.TaskType, out _))
        {
            logger.LogWarning("Skipping schedule {ScheduleKey}; task_type {TaskType} is not registered.", task.Key, task.TaskType);
            return;
        }

        await DeleteAsync(scheduler, task.Key, cancellationToken);
        await scheduler.ScheduleJob(
            QuartzScheduleFactory.BuildJob(task),
            QuartzScheduleFactory.BuildTrigger(task),
            default,
            cancellationToken);
        logger.LogInformation("Registered schedule {ScheduleKey} ({TaskType}) in Quartz.", task.Key, task.TaskType);
    }

    public async Task DeleteAsync(IScheduler scheduler, string scheduleKey, CancellationToken cancellationToken = default)
        => _ = await scheduler.DeleteJob(JobKeys.ForSchedule(scheduleKey), cancellationToken);
}
