using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NodaTime;
using Quartz;
using Scheduler.Triggers;
using Shared.Messaging;

namespace Scheduler.Services;

/// <summary>
/// On startup: reload active schedules from DataBridge, register Quartz triggers
/// for them, then publish one catch-up command per overdue schedule whose policy is
/// <c>Coalesce</c>.
///
/// Runs as a one-shot <see cref="BackgroundService"/> (idles after the initial
/// hydration) so Quartz's hosted service has time to come up before we call
/// <see cref="IScheduler.ScheduleJob(IJobDetail, ITrigger, CancellationToken)"/>.
/// </summary>
public sealed class ScheduleHydrationService(
    ISchedulerFactory schedulerFactory,
    IMessageBus messageBus,
    IJetStreamPublisher jetStreamPublisher,
    IClock clock,
    ILogger<ScheduleHydrationService> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Backoff schedule for the initial DataBridge requests. Each entry is the wait
    /// BEFORE the next attempt; total ceiling ~30s so AppHost startup eventually
    /// converges. <see cref="NatsNoRespondersException"/> is the only error we retry.
    /// </summary>
    private static readonly TimeSpan[] StartupBackoff =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scheduler = await schedulerFactory.GetScheduler(stoppingToken);

            var active = await FetchListAsync<ScheduleListActiveRequestMessage>(
                ScheduleSubjects.ListActive, new(), stoppingToken);

            if (active is null)
            {
                logger.LogWarning("Schedule hydration: no response from DataBridge; Quartz starts with no triggers.");
                return;
            }

            var registered = 0;
            foreach (var schedule in active)
            {
                if (await TryRegisterAsync(scheduler, schedule, stoppingToken))
                    registered++;
            }
            logger.LogInformation("Schedule hydration registered {Registered} of {Total} active schedules.",
                registered, active.Count);

            var overdue = await FetchListAsync<ScheduleListOverdueRequestMessage>(
                ScheduleSubjects.ListOverdue, new(), stoppingToken);
            if (overdue is null)
                return;

            var caughtUp = 0;
            foreach (var schedule in overdue.Where(s => s.CatchupPolicy == ScheduleCatchupPolicyDto.Coalesce))
            {
                if (await TryPublishCatchupAsync(schedule, stoppingToken))
                    caughtUp++;
            }
            logger.LogInformation("Schedule hydration published {CaughtUp} catch-up commands for overdue schedules.",
                caughtUp);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schedule hydration failed; Scheduler will run with whatever Quartz state was registered.");
        }
    }

    private async Task<IReadOnlyList<ScheduledTaskDto>?> FetchListAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                var resp = await messageBus.RequestAsync<TRequest, ScheduleOperationResponseMessage>(
                    subject, request, RequestTimeout, ct);
                if (resp is null || !resp.Success)
                {
                    logger.LogWarning("Schedule hydration request to '{Subject}' failed: {Code} {Message}",
                        subject, resp?.ErrorCode, resp?.ErrorMessage);
                    return null;
                }
                return resp.Items ?? Array.Empty<ScheduledTaskDto>();
            }
            catch (NatsNoRespondersException) when (attempt < StartupBackoff.Length)
            {
                // DataBridge process is up but ScheduleCrudConsumerService hasn't
                // subscribed to the subject yet. Wait a bit and retry — this only
                // happens on cold start.
                var delay = StartupBackoff[attempt++];
                logger.LogInformation(
                    "Schedule hydration: no responders for '{Subject}' yet (attempt {Attempt}); retrying in {Delay}.",
                    subject, attempt, delay);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Schedule hydration request to '{Subject}' threw.", subject);
                return null;
            }
        }
    }

    private async Task<bool> TryRegisterAsync(IScheduler scheduler, ScheduledTaskDto schedule, CancellationToken ct)
    {
        if (!TaskTypeRegistry.TryGetJobType(schedule.TaskType, out var jobType))
        {
            logger.LogWarning("Schedule hydration skipping unknown task_type '{TaskType}' on schedule '{Key}'.",
                schedule.TaskType, schedule.Key);
            return false;
        }

        try
        {
            var jobKey = new JobKey(schedule.Key);
            var trigger = BuildTrigger(schedule);
            if (trigger is null)
            {
                logger.LogWarning("Schedule '{Key}' has neither cron nor interval; skipping.", schedule.Key);
                return false;
            }

            var jobDetail = JobBuilder.Create(jobType)
                .WithIdentity(jobKey)
                .UsingJobData(OrphanMetadataCleanupTriggerJob.ScheduleKeyDataKey, schedule.Key)
                .Build();

            await scheduler.ScheduleJob(jobDetail, [trigger], replace: true, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schedule hydration failed to register '{Key}'.", schedule.Key);
            return false;
        }
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

    private async Task<bool> TryPublishCatchupAsync(ScheduledTaskDto schedule, CancellationToken ct)
    {
        if (schedule.TaskType != TaskTypeRegistry.OrphanMetadataCleanup)
        {
            // Other task types will get their own catch-up paths as they're added.
            return false;
        }

        try
        {
            // Compose the catch-up window from the schedule's last NextDueAt (the
            // window that was missed) so the dedupe key matches what would have
            // fired if the Scheduler had been up.
            var window = (schedule.NextDueAt ?? clock.GetCurrentInstant()).ToDateTimeOffset();
            var idempotencyKey = OrphanMetadataCleanupTriggerJob.BuildIdempotencyKey(schedule.Key, window);

            await jetStreamPublisher.PublishAsync(
                ScheduleSubjects.OrphanMetadataCleanupRequest,
                new OrphanMetadataCleanupRequested
                {
                    ScheduleKey = schedule.Key,
                    CorrelationId = Guid.NewGuid(),
                    IdempotencyKey = idempotencyKey,
                    TriggeredAt = clock.GetCurrentInstant()
                },
                messageId: idempotencyKey,
                cancellationToken: ct);

            logger.LogInformation(
                "Catch-up published for ScheduleKey {ScheduleKey} IdempotencyKey {IdempotencyKey}",
                schedule.Key, idempotencyKey);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catch-up publish failed for ScheduleKey '{Key}'", schedule.Key);
            return false;
        }
    }
}
