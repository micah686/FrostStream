using Conduit.NATS;
using FluentStorage.Storage;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Shared.Storage;

namespace Worker.Services;

/// <summary>
/// Handles physical deletion of a media object from a storage backend on behalf of the
/// DataBridge media-delete orchestration. Deletion is idempotent: removing an already-missing
/// path is treated as success by the underlying storage provider, so the orchestrator can
/// safely retry after a partial failure.
/// </summary>
public sealed class MediaFileDeleteConsumerService(
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    ILogger<MediaFileDeleteConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "worker-media-file-delete";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<DeleteMediaFileRequest>(
            messageBus,
            MediaFileSubjects.Delete,
            HandleDeleteAsync,
            QueueGroup,
            stoppingToken);

        logger.LogInformation("Subscribed to media file delete requests.");
    }

    private async Task HandleDeleteAsync(IMessageContext<DeleteMediaFileRequest> context)
    {
        var request = context.Message;
        try
        {
            ValidateRequest(request);

            var storage = await blobStorageProvider.GetAsync(request.StorageKey);
            await storage.DeleteObjects([request.StoragePath]);

            logger.LogInformation(
                "Deleted media file {StorageKey}:{StoragePath}.",
                request.StorageKey,
                request.StoragePath);

            await context.RespondAsync(new DeleteMediaFileResponse { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed deleting media file {StorageKey}:{StoragePath}.",
                request.StorageKey,
                request.StoragePath);
            await context.RespondAsync(new DeleteMediaFileResponse
            {
                Success = false,
                ErrorCode = ex is ArgumentException ? "validation" : "storage_error",
                ErrorMessage = ex.Message
            });
        }
    }

    private static void ValidateRequest(DeleteMediaFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(request.StorageKey));
        }

        if (string.IsNullOrWhiteSpace(request.StoragePath))
        {
            throw new ArgumentException("Storage path is required.", nameof(request.StoragePath));
        }
    }
}
