using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Scheduler.Triggers;
using Shared.Messaging;

namespace Scheduler.Services;

/// <summary>
/// Subscribes to <see cref="ScheduleSubjects.ScheduleChanged"/> broadcasts and keeps
/// the in-memory Quartz scheduler aligned with DataBridge's schedule table.
///
/// On Created/Updated: fetch the latest definition via
/// <see cref="ScheduleSubjects.GetSchedule"/> and re-register the Quartz job/trigger
/// (replacing any existing one). On Deleted: remove the Quartz job.
/// </summary>
public sealed class ScheduleChangeListener(
    ISchedulerFactory schedulerFactory,
    IMessageBus messageBus,
    ILogger<ScheduleChangeListener> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<ScheduleChangedMessage>(
            ScheduleSubjects.ScheduleChanged,
            HandleAsync,
            queueGroup: null,  // fan-out: every Scheduler replica refreshes its own state
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to {Subject} for schedule-change refreshes.", ScheduleSubjects.ScheduleChanged);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.StopAsync(cancellationToken);
            await _subscription.DisposeAsync();
            _subscription = null;
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(IMessageContext<ScheduleChangedMessage> context)
    {
        var msg = context.Message;
        var scheduler = await schedulerFactory.GetScheduler();

        try
        {
            switch (msg.Change)
            {
                case ScheduleChangeKind.Deleted:
                    await UnscheduleAsync(scheduler, msg.Key);
                    break;
                case ScheduleChangeKind.Created:
                case ScheduleChangeKind.Updated:
                    await RefreshAsync(scheduler, msg.Key);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling schedule-change for '{Key}' ({Change})", msg.Key, msg.Change);
        }
    }

    private async Task UnscheduleAsync(IScheduler scheduler, string key)
    {
        var deleted = await scheduler.DeleteJob(new JobKey(key));
        logger.LogInformation("Schedule '{Key}' deleted: Quartz job removed = {Deleted}", key, deleted);
    }

    private async Task RefreshAsync(IScheduler scheduler, string key)
    {
        var resp = await messageBus.RequestAsync<ScheduleGetRequestMessage, ScheduleOperationResponseMessage>(
            ScheduleSubjects.GetSchedule,
            new ScheduleGetRequestMessage { Key = key },
            RequestTimeout);

        if (resp is null || !resp.Success || resp.Entity is null)
        {
            logger.LogWarning("Schedule refresh: could not load '{Key}' ({Code} {Message}); leaving Quartz state untouched.",
                key, resp?.ErrorCode, resp?.ErrorMessage);
            return;
        }

        var schedule = resp.Entity;

        // Disabled schedules get unscheduled from Quartz so they stop firing.
        if (!schedule.Enabled)
        {
            await UnscheduleAsync(scheduler, key);
            return;
        }

        if (!TaskTypeRegistry.TryGetJobType(schedule.TaskType, out var jobType))
        {
            logger.LogWarning("Schedule refresh: unknown task_type '{TaskType}' on '{Key}'; skipping.",
                schedule.TaskType, key);
            return;
        }

        var trigger = BuildTrigger(schedule);
        if (trigger is null)
        {
            logger.LogWarning("Schedule '{Key}' has neither cron nor interval; cannot register trigger.", key);
            return;
        }

        var jobDetail = JobBuilder.Create(jobType)
            .WithIdentity(new JobKey(key))
            .UsingJobData(OrphanMetadataCleanupTriggerJob.ScheduleKeyDataKey, key)
            .Build();

        await scheduler.ScheduleJob(jobDetail, [trigger], replace: true);
        logger.LogInformation("Schedule '{Key}' refreshed: Quartz trigger replaced.", key);
    }

    private static ITrigger? BuildTrigger(ScheduledTaskDto schedule)
    {
        var triggerKey = new TriggerKey($"{schedule.Key}-trigger");
        var builder = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(schedule.Key);

        if (!string.IsNullOrWhiteSpace(schedule.Cron))
        {
            var cronBuilder = CronScheduleBuilder.CronSchedule(schedule.Cron);
            try
            {
                cronBuilder = cronBuilder.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone));
            }
            catch (TimeZoneNotFoundException)
            {
                cronBuilder = cronBuilder.InTimeZone(TimeZoneInfo.Utc);
            }
            return builder.WithSchedule(cronBuilder).Build();
        }

        if (schedule.IntervalSeconds is { } seconds && seconds > 0)
        {
            return builder
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(seconds)).RepeatForever())
                .StartNow()
                .Build();
        }

        return null;
    }
}
