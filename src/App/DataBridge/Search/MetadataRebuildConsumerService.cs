using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class MetadataRebuildConsumerService(
    IMessageBus messageBus,
    IMetadataRebuildCoordinator rebuildCoordinator,
    ILogger<MetadataRebuildConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MetadataSyncRebuildRequestMessage>(
            messageBus,
            MetadataSyncSubjects.SyncRebuild,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata sync rebuild subject.");
    }

    private async Task HandleAsync(IMessageContext<MetadataSyncRebuildRequestMessage> context)
    {
        var result = rebuildCoordinator.StartRebuild("manual request");
        await context.RespondAsync(new MetadataSyncRebuildResponseMessage
        {
            Accepted = result.Accepted,
            ErrorMessage = result.ErrorMessage
        });
    }
}
