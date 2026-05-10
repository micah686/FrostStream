using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace Shared.Storage;

/// <summary>
/// Listens for storage config change notifications and evicts the affected
/// entry from the local <see cref="IBlobStorageProvider"/> cache so the next
/// request rebuilds against the new config.
/// </summary>
public sealed class StorageConfigChangedSubscriber(
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    ILogger<StorageConfigChangedSubscriber> logger) : BackgroundService
{
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<StorageConfigChangedMessage>(
            StorageSubjects.StorageConfigChanged,
            HandleAsync,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to storage config change notifications.");

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

    private Task HandleAsync(IMessageContext<StorageConfigChangedMessage> context)
    {
        blobStorageProvider.Invalidate(context.Message.Key);
        return Task.CompletedTask;
    }
}
