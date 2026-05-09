using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class TypesenseSyncConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ITypesenseIndexService indexService,
    ILogger<TypesenseSyncConsumerService> logger) : BackgroundService
{
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<MetadataSyncUpsertMessage>(
            MetadataSyncSubjects.SyncUpsert,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata sync upsert subject.");

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

    private async Task HandleAsync(IMessageContext<MetadataSyncUpsertMessage> context)
    {
        var mediaGuid = context.Message.MediaGuid;
        var mediaGuidString = TypesenseSearchHelpers.NormalizeGuid(mediaGuid);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IMediaDocumentQuery>();

            var mediaTask = query.GetMediaByGuidAsync(mediaGuid);
            var commentsTask = query.GetCommentsByMediaGuidAsync(mediaGuid);
            var captionsTask = query.GetCaptionsByMediaGuidAsync(mediaGuid);

            await Task.WhenAll(mediaTask, commentsTask, captionsTask);

            var media = await mediaTask;
            if (media is null)
            {
                logger.LogWarning("Skipping Typesense sync for missing media {MediaGuid}.", mediaGuid);
                return;
            }

            await indexService.UpsertMediaAsync(media);
            await indexService.DeleteCommentsByMediaGuidAsync(mediaGuidString);
            await indexService.BulkImportCommentsAsync(await commentsTask);
            await indexService.DeleteCaptionsByMediaGuidAsync(mediaGuidString);
            await indexService.BulkImportCaptionsAsync(await captionsTask);

            logger.LogInformation("Synced Typesense metadata documents for {MediaGuid}.", mediaGuid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed syncing Typesense metadata documents for {MediaGuid}.", mediaGuid);
        }
    }
}
