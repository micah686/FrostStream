using Conduit.NATS;
using FluentStorage.Blobs;
using Microsoft.Extensions.Logging;
using Shared.Messaging;
using Shared.Storage;

namespace Worker.Services;

public sealed class OrphanCleanupConsumerService(
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    ILogger<OrphanCleanupConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "worker-orphan-cleanup";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MoveOrphanedFileRequest>(
            messageBus,
            OrphanCleanupSubjects.MoveFile,
            HandleMoveAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<DeleteOrphanedFileRequest>(
            messageBus,
            OrphanCleanupSubjects.DeleteFile,
            HandleDeleteAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<RestoreOrphanedFileRequest>(
            messageBus,
            OrphanCleanupSubjects.RestoreFile,
            HandleRestoreAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<OrphanFileExistsRequest>(
            messageBus,
            OrphanCleanupSubjects.FileExists,
            HandleFileExistsAsync,
            QueueGroup,
            stoppingToken);

        logger.LogInformation("Subscribed to orphan cleanup move/delete/restore requests.");
    }

    private async Task HandleMoveAsync(IMessageContext<MoveOrphanedFileRequest> context)
    {
        var request = context.Message;
        try
        {
            ValidateMoveRequest(request);

            var storage = await blobStorageProvider.GetAsync(request.StorageKey);
            await using (var source = await storage.OpenReadAsync(request.OriginalStoragePath))
            {
                await storage.WriteAsync(request.OrphanStoragePath, source, append: false);
            }

            await storage.DeleteAsync([request.OriginalStoragePath]);

            logger.LogInformation(
                "Moved orphan file {StorageKey}:{OriginalPath} to {OrphanPath} for orphan row {OrphanId}.",
                request.StorageKey,
                request.OriginalStoragePath,
                request.OrphanStoragePath,
                request.OrphanId);

            await context.RespondAsync(new MoveOrphanedFileResponse { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed moving orphan file {StorageKey}:{OriginalPath} to {OrphanPath} for orphan row {OrphanId}.",
                request.StorageKey,
                request.OriginalStoragePath,
                request.OrphanStoragePath,
                request.OrphanId);
            await context.RespondAsync(new MoveOrphanedFileResponse
            {
                Success = false,
                ErrorCode = ErrorCodeFor(ex),
                ErrorMessage = ex.Message
            });
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<DeleteOrphanedFileRequest> context)
    {
        var request = context.Message;
        try
        {
            ValidateDeleteRequest(request);

            var storage = await blobStorageProvider.GetAsync(request.StorageKey);
            await storage.DeleteAsync([request.OrphanStoragePath]);

            logger.LogInformation(
                "Deleted orphan file {StorageKey}:{OrphanPath} for orphan row {OrphanId}.",
                request.StorageKey,
                request.OrphanStoragePath,
                request.OrphanId);

            await context.RespondAsync(new DeleteOrphanedFileResponse { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed deleting orphan file {StorageKey}:{OrphanPath} for orphan row {OrphanId}.",
                request.StorageKey,
                request.OrphanStoragePath,
                request.OrphanId);
            await context.RespondAsync(new DeleteOrphanedFileResponse
            {
                Success = false,
                ErrorCode = ErrorCodeFor(ex),
                ErrorMessage = ex.Message
            });
        }
    }

    private async Task HandleRestoreAsync(IMessageContext<RestoreOrphanedFileRequest> context)
    {
        var request = context.Message;
        try
        {
            ValidateRestoreRequest(request);

            var storage = await blobStorageProvider.GetAsync(request.StorageKey);
            if (await storage.ExistsAsync(request.OriginalStoragePath))
            {
                throw new OrphanCleanupException("conflict", "Destination storage path already exists.");
            }

            await using (var source = await storage.OpenReadAsync(request.OrphanStoragePath))
            {
                await storage.WriteAsync(request.OriginalStoragePath, source, append: false);
            }

            await storage.DeleteAsync([request.OrphanStoragePath]);

            logger.LogInformation(
                "Restored orphan file {StorageKey}:{OrphanPath} to {OriginalPath} for orphan row {OrphanId}.",
                request.StorageKey,
                request.OrphanStoragePath,
                request.OriginalStoragePath,
                request.OrphanId);

            await context.RespondAsync(new RestoreOrphanedFileResponse { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed restoring orphan file {StorageKey}:{OrphanPath} to {OriginalPath} for orphan row {OrphanId}.",
                request.StorageKey,
                request.OrphanStoragePath,
                request.OriginalStoragePath,
                request.OrphanId);
            await context.RespondAsync(new RestoreOrphanedFileResponse
            {
                Success = false,
                ErrorCode = ErrorCodeFor(ex),
                ErrorMessage = ex.Message
            });
        }
    }

    private async Task HandleFileExistsAsync(IMessageContext<OrphanFileExistsRequest> context)
    {
        var request = context.Message;
        try
        {
            ValidateFileExistsRequest(request);

            var storage = await blobStorageProvider.GetAsync(request.StorageKey);
            var exists = await storage.ExistsAsync(request.StoragePath);
            await context.RespondAsync(new OrphanFileExistsResponse { Success = true, Exists = exists });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed checking orphan cleanup file existence for {StorageKey}:{StoragePath}.",
                request.StorageKey,
                request.StoragePath);
            await context.RespondAsync(new OrphanFileExistsResponse
            {
                Success = false,
                Exists = false,
                ErrorCode = ErrorCodeFor(ex),
                ErrorMessage = ex.Message
            });
        }
    }

    private static void ValidateMoveRequest(MoveOrphanedFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(request.StorageKey));
        }

        if (string.IsNullOrWhiteSpace(request.OriginalStoragePath))
        {
            throw new ArgumentException("Original storage path is required.", nameof(request.OriginalStoragePath));
        }

        ValidateOrphanPath(request.OrphanStoragePath);
    }

    private static void ValidateDeleteRequest(DeleteOrphanedFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(request.StorageKey));
        }

        ValidateOrphanPath(request.OrphanStoragePath);
    }

    private static void ValidateRestoreRequest(RestoreOrphanedFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(request.StorageKey));
        }

        ValidateOrphanPath(request.OrphanStoragePath);
        ValidateNonOrphanPath(request.OriginalStoragePath, "Original storage path is required.");
    }

    private static void ValidateFileExistsRequest(OrphanFileExistsRequest request)
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

    private static void ValidateOrphanPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Orphan storage path is required.", nameof(path));
        }

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("orphaned/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Orphan storage path must be under orphaned/.", nameof(path));
        }
    }

    private static void ValidateNonOrphanPath(string path, string requiredMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(requiredMessage, nameof(path));
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("orphaned/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Destination storage path must not be under orphaned/.", nameof(path));
        }
    }

    private static string ErrorCodeFor(Exception exception)
        => exception is OrphanCleanupException orphanException
            ? orphanException.ErrorCode
            : exception is ArgumentException
                ? "validation"
                : "storage_error";

    private sealed class OrphanCleanupException(string errorCode, string message) : Exception(message)
    {
        public string ErrorCode { get; } = errorCode;
    }
}
