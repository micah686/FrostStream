using DataBridge;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class SearchSyncConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<SearchSyncConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MetadataSyncUpsertMessage>(
            messageBus,
            MetadataSyncSubjects.SyncUpsert,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata sync upsert subject.");
    }

    private Task HandleAsync(IMessageContext<MetadataSyncUpsertMessage> context)
        => scopeFactory.WithScopedAsync<DataBridge.Persistence.Indexing.PendingIndexStore>(store => store.RequestAsync(context.Message.MediaGuid));
}
