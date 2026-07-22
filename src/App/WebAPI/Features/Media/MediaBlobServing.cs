using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Storage;

namespace WebAPI.Features.Media;

/// <summary>
/// Shared open-and-serve helpers for blobs referenced by resolved media locations. Collapses the
/// resolve → open → content-type → range-enabled File() → storage-error mapping pattern that every
/// media endpoint repeats.
/// </summary>
public static class MediaBlobServing
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public static string InferContentType(string storagePath, string fallback = "application/octet-stream")
        => ContentTypeProvider.TryGetContentType(storagePath, out var resolved) ? resolved : fallback;

    /// <summary>
    /// Opens a blob and returns it as a range-enabled file response. Maps a missing blob to 404 and
    /// an unreadable one to 502 using the supplied subject noun (e.g. "media stream", "thumbnail").
    /// </summary>
    public static async Task<IActionResult> ServeBlobAsync(
        this ControllerBase controller,
        IBlobStorageProvider blobStorageProvider,
        ILogger logger,
        string storageKey,
        string storagePath,
        string subject,
        string? contentType = null,
        bool enableRangeProcessing = true,
        string? cacheControl = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var storage = await blobStorageProvider.GetAsync(storageKey, cancellationToken);
            var stream = await storage.OpenRead(storagePath, cancellationToken);
            if (stream is null)
            {
                return controller.NotFound($"The selected {subject} is missing from storage.");
            }

            if (cacheControl is not null)
            {
                controller.Response.Headers.CacheControl = cacheControl;
            }

            return controller.File(
                stream,
                contentType ?? InferContentType(storagePath),
                enableRangeProcessing: enableRangeProcessing && stream.CanSeek);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return controller.NotFound($"The selected {subject} is missing from storage.");
        }
        catch (DirectoryNotFoundException)
        {
            return controller.NotFound($"The selected {subject} is missing from storage.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed opening {Subject} from storage {StorageKey} at {StoragePath}.",
                subject,
                storageKey,
                storagePath);

            return new ObjectResult($"The selected {subject} could not be opened.")
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }

    /// <summary>Opens a blob for in-process reading, mapping "missing" to null instead of throwing.</summary>
    public static async Task<Stream?> OpenBlobOrNullAsync(
        IBlobStorageProvider blobStorageProvider,
        string storageKey,
        string? storagePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return null;
        }

        try
        {
            var storage = await blobStorageProvider.GetAsync(storageKey, cancellationToken);
            return await storage.OpenRead(storagePath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static async Task<string?> ReadBlobTextAsync(
        IBlobStorageProvider blobStorageProvider,
        string storageKey,
        string? storagePath,
        CancellationToken cancellationToken)
    {
        var stream = await OpenBlobOrNullAsync(blobStorageProvider, storageKey, storagePath, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        await using (stream)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
    }
}
