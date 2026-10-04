using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Hosting;

namespace Shared.Messaging;

public abstract class SubscriptionBackgroundService : BackgroundService, IApplicationHandlerInitialization
{
    private readonly List<ISubscription> _subscriptions = [];
    private readonly object subscriptionsGate = new();
    private bool stopping;
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
        lock (subscriptionsGate) stopping = true;
        await base.StopAsync(cancellationToken);
        // Host shutdown can time out while a broker finishes registration, or StopAsync can
        // be called twice. Snapshot ownership once; late subscriptions dispose themselves.
        ISubscription[] subscriptions;
        lock (subscriptionsGate)
        {
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }
    }

    protected abstract Task RegisterSubscriptionsAsync(CancellationToken stoppingToken);

    protected async Task SubscribeAsync<T>(
        IMessageBus messageBus,
        string subject,
        Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null,
        CancellationToken cancellationToken = default)
    {
        var subscription = await messageBus.SubscribeAsync(subject, handler, queueGroup, cancellationToken);
        lock (subscriptionsGate)
        {
            if (!stopping)
            {
                _subscriptions.Add(subscription);
                return;
            }
        }
        await subscription.StopAsync(CancellationToken.None);
        await subscription.DisposeAsync();
    }
}
