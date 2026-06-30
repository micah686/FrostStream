using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class MediaDeleteConsumerService(
    IMessageBus messageBus,
    MediaDeleteExecutor deleteExecutor,
    ILogger<MediaDeleteConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MediaDeleteRequest>(
            messageBus,
            MediaDeleteSubjects.Delete,
            HandleDeleteAsync,
            MetadataSubjects.ProcessorsQueueGroup,
            stoppingToken);
        await SubscribeAsync<MediaDeleteForStorageKeyRequest>(
            messageBus,
            MediaDeleteSubjects.DeleteForStorageKey,
            HandleDeleteForStorageKeyAsync,
            MetadataSubjects.ProcessorsQueueGroup,
            stoppingToken);

        logger.LogInformation("Subscribed to media delete requests.");
    }

    private async Task HandleDeleteAsync(IMessageContext<MediaDeleteRequest> context)
    {
        try
        {
            await context.RespondAsync(await deleteExecutor.DeleteMediaAsync(context.Message.MediaGuid));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting media {MediaGuid}.", context.Message.MediaGuid);
            await context.RespondAsync(InternalError());
        }
    }

    private async Task HandleDeleteForStorageKeyAsync(IMessageContext<MediaDeleteForStorageKeyRequest> context)
    {
        try
        {
            await context.RespondAsync(await deleteExecutor.DeleteMediaForStorageKeyAsync(
                context.Message.MediaGuid,
                context.Message.StorageKey));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed deleting media {MediaGuid} on storage key '{StorageKey}'.",
                context.Message.MediaGuid,
                context.Message.StorageKey);
            await context.RespondAsync(InternalError());
        }
    }

    private static MediaDeleteResponse InternalError()
        => new()
        {
            Success = false,
            ErrorCode = "unavailable",
            ErrorMessage = "Internal media delete service error."
        };
}
