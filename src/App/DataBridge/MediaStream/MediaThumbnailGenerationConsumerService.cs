using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class MediaThumbnailGenerationConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<MediaThumbnailGenerationConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MissingMediaThumbnailsRequest>(
            messageBus,
            MediaThumbnailGenerationSubjects.ListMissing,
            HandleListAsync,
            MediaThumbnailGenerationSubjects.QueueGroup,
            stoppingToken);

        await SubscribeAsync<MediaThumbnailGeneratedRequest>(
            messageBus,
            MediaThumbnailGenerationSubjects.Complete,
            HandleCompleteAsync,
            MediaThumbnailGenerationSubjects.QueueGroup,
            stoppingToken);
    }

    private async Task HandleListAsync(IMessageContext<MissingMediaThumbnailsRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IMediaThumbnailGenerationService>();
            var items = await service.ListMissingAsync(
                context.Message.AccountId,
                context.Message.AfterMediaGuid,
                context.Message.Limit);
            await context.RespondAsync(new MissingMediaThumbnailsResponse { Success = true, Items = items });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing missing thumbnails for account {AccountId}.", context.Message.AccountId);
            await context.RespondAsync(new MissingMediaThumbnailsResponse
            {
                Success = false,
                ErrorMessage = "Could not list media missing thumbnails."
            });
        }
    }

    private async Task HandleCompleteAsync(IMessageContext<MediaThumbnailGeneratedRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var updated = await scope.ServiceProvider.GetRequiredService<IMediaThumbnailGenerationService>()
                .CompleteAsync(context.Message.MediaGuid, context.Message.StorageKey, context.Message.StoragePath);
            if (updated)
                await PublishMetadataSyncAsync(context.Message.MediaGuid);

            await context.RespondAsync(new MediaThumbnailGeneratedResponse
            {
                Success = updated,
                ErrorMessage = updated ? null : "Media was not found or already has a thumbnail."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed attaching generated thumbnail for {MediaGuid}.", context.Message.MediaGuid);
            await context.RespondAsync(new MediaThumbnailGeneratedResponse
            {
                Success = false,
                ErrorMessage = "Could not attach the generated thumbnail."
            });
        }
    }

    private async Task PublishMetadataSyncAsync(Guid mediaGuid)
    {
        try
        {
            await messageBus.PublishAsync(
                MetadataSyncSubjects.SyncUpsert,
                new MetadataSyncUpsertMessage { MediaGuid = mediaGuid });
        }
        catch (Exception ex)
        {
            // The database remains authoritative and the list query repairs a stale
            // thumbnail projection. Do not report generation as failed after upload.
            logger.LogWarning(ex, "Failed publishing metadata sync for generated thumbnail {MediaGuid}.", mediaGuid);
        }
    }
}
