using Conduit.NATS;
using NodaTime;
using Quartz;
using Scheduler.Databridge;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shared.Database;
using Shared.Messaging;

namespace Scheduler.Services;

public sealed class ScheduleHydrationService(
    IDatabridgeClient databridgeClient,
    IJetStreamPublisher publisher,
    ISchedulerFactory schedulerFactory,
    IQuartzJobRegistrar registrar,
    IClock clock,
    ILogger<ScheduleHydrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var activeResponse = await RequestWithRetryAsync(
            () => databridgeClient.ListActiveSchedulesAsync(stoppingToken),
            stoppingToken);
        if (activeResponse is null || !activeResponse.Success)
        {
            logger.LogWarning("Could not hydrate schedules: {Error}", activeResponse?.ErrorMessage ?? "no response");
            return;
        }

        var scheduler = await schedulerFactory.GetScheduler(stoppingToken);
        foreach (var task in activeResponse.Items ?? Array.Empty<ScheduledTaskDto>())
        {
            await registrar.RegisterAsync(scheduler, task, stoppingToken);
        }

        var overdueResponse = await RequestWithRetryAsync(
            () => databridgeClient.ListOverdueSchedulesAsync(stoppingToken),
            stoppingToken);
        if (overdueResponse is not { Success: true })
        {
            logger.LogWarning("Could not check overdue schedules: {Error}", overdueResponse?.ErrorMessage ?? "no response");
            return;
        }

        foreach (var task in overdueResponse.Items ?? Array.Empty<ScheduledTaskDto>())
        {
            await TryPublishCatchupAsync(task, stoppingToken);
        }
    }

    private async Task TryPublishCatchupAsync(ScheduledTaskDto task, CancellationToken cancellationToken)
    {
        if (task.CatchupPolicy != ScheduleCatchupPolicy.Coalesce ||
            !string.Equals(task.TaskType, TaskTypeRegistry.OrphanMetadataCleanup, StringComparison.OrdinalIgnoreCase) ||
            task.NextDueAt is not { } dueWindow)
        {
            return;
        }

        var idempotencyKey = $"{TaskTypeRegistry.OrphanMetadataCleanup}:{task.Key}:{dueWindow:uuuu-MM-ddTHH:mm:ss'Z'}";
        await publisher.PublishAsync(
            ScheduleSubjects.OrphanMetadataCleanupRequest,
            new OrphanMetadataCleanupRequested
            {
                ScheduleKey = task.Key,
                TaskType = task.TaskType,
                DueWindowUtc = dueWindow,
                IdempotencyKey = idempotencyKey,
                OccurredAt = clock.GetCurrentInstant()
            },
            messageId: idempotencyKey,
            cancellationToken: cancellationToken);
    }

    private async Task<ScheduleOperationResponseMessage?> RequestWithRetryAsync(
        Func<Task<ScheduleOperationResponseMessage?>> request,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            try
            {
                return await request();
            }
            catch (Exception ex) when (attempt < 7 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Schedule request failed on attempt {Attempt}; retrying.", attempt);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 15000));
            }
        }

        return null;
    }
}
