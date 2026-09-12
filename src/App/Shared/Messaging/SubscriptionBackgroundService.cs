using Conduit.NATS;
using Microsoft.Extensions.Hosting;

namespace Shared.Messaging;

public abstract class SubscriptionBackgroundService : BackgroundService
{
    private readonly List<ISubscription> _subscriptions = [];
    private readonly object _subscriptionsLock = new();
    private bool _stopping;

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterSubscriptionsAsync(stoppingToken);

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
        ISubscription[] subscriptions;
        lock (_subscriptionsLock)
        {
            _stopping = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

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
        var subscription = await messageBus.SubscribeAsync(
            subject,
            handler,
            queueGroup,
            cancellationToken);

        lock (_subscriptionsLock)
        {
            if (!_stopping)
            {
                _subscriptions.Add(subscription);
                return;
            }
        }

        await subscription.StopAsync(CancellationToken.None);
        await subscription.DisposeAsync();
    }
}
