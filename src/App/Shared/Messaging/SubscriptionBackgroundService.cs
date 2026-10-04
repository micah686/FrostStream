using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Hosting;

namespace Shared.Messaging;

public abstract class SubscriptionBackgroundService : BackgroundService
{
    private readonly List<ISubscription> _subscriptions = [];
    private readonly TaskCompletionSource registrationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes only after all subscriptions are installed; local hosts await this before scheduling.</summary>
    public Task RegistrationCompleted => registrationCompleted.Task;

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RegisterSubscriptionsAsync(stoppingToken);
            registrationCompleted.TrySetResult();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            registrationCompleted.TrySetCanceled(stoppingToken);
            throw;
        }
        catch (Exception ex)
        {
            registrationCompleted.TrySetException(ex);
            throw;
        }

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
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    protected abstract Task RegisterSubscriptionsAsync(CancellationToken stoppingToken);

    protected async Task SubscribeAsync<T>(
        IMessageBus messageBus,
        string subject,
        Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null,
        CancellationToken cancellationToken = default)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync(
            subject,
            handler,
            queueGroup,
            cancellationToken));
    }
}
