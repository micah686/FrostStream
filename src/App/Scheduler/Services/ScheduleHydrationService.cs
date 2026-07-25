using Quartz;
using Scheduler.Databridge;
using Scheduler.Scheduling;
using Shared.Database;
using Shared.Messaging;

namespace Scheduler.Services;

public sealed class ScheduleHydrationService(
    IDatabridgeClient databridgeClient,
    ISchedulerFactory schedulerFactory,
    IQuartzJobRegistrar registrar,
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
