using FlySwattr.NATS.Abstractions;
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
    ILogger<StorageConfigChangedSubscriber> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<StorageConfigChangedMessage>(
            messageBus,
            StorageSubjects.StorageConfigChanged,
            HandleAsync,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to storage config change notifications.");
    }

    private Task HandleAsync(IMessageContext<StorageConfigChangedMessage> context)
    {
        blobStorageProvider.Invalidate(context.Message.Key);
        return Task.CompletedTask;
    }
}
