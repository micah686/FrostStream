using FlySwattr.NATS.Abstractions;
using Quartz;
using Scheduler.Databridge;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shared.Messaging;

namespace Scheduler.Services;

public sealed class ScheduleChangeListener(
    IMessageBus messageBus,
    IDatabridgeClient databridgeClient,
    ISchedulerFactory schedulerFactory,
    IQuartzJobRegistrar registrar,
    ILogger<ScheduleChangeListener> logger) : BackgroundService
{
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<ScheduleChangedMessage>(
            ScheduleSubjects.Changed,
            HandleChangedAsync,
            queueGroup: null,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to schedule change notifications.");

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
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleChangedAsync(IMessageContext<ScheduleChangedMessage> context)
    {
        var change = context.Message;
        var scheduler = await schedulerFactory.GetScheduler();

        if (change.Kind == ScheduleChangeKind.Deleted)
        {
            await registrar.DeleteAsync(scheduler, change.Key);
            logger.LogInformation("Deleted Quartz schedule {ScheduleKey}.", change.Key);
            return;
        }

        var response = await databridgeClient.GetScheduleAsync(change.Key);

        if (response is not { Success: true, Entity: { } task })
        {
            await registrar.DeleteAsync(scheduler, change.Key);
            logger.LogWarning("Removed Quartz schedule {ScheduleKey}; schedule lookup failed after change event.", change.Key);
            return;
        }

        if (!task.Enabled)
        {
            await registrar.DeleteAsync(scheduler, task.Key);
            logger.LogInformation("Removed disabled Quartz schedule {ScheduleKey}.", task.Key);
            return;
        }

        if (!TaskTypeRegistry.TryGetJobType(task.TaskType, out _))
        {
            logger.LogWarning("Ignoring schedule {ScheduleKey}; task_type {TaskType} is not registered.", task.Key, task.TaskType);
            return;
        }

        await registrar.RegisterAsync(scheduler, task);
    }
}
