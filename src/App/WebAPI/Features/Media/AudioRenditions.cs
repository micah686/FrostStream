using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;

namespace WebAPI.Features.Media;

/// <summary>
/// Resolves (and optionally queues) cached audio renditions through DataBridge. Shared by the
/// progressive watch endpoint's audio mode and every HLS endpoint.
/// </summary>
public sealed class AudioRenditionResolver(IMessageBus messageBus, ILogger<AudioRenditionResolver> logger)
{
    private static readonly TimeSpan AudioQueryTimeout = TimeSpan.FromSeconds(30);

    public async Task<(IActionResult? Error, AudioRenditionDto? Rendition)> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await messageBus.RequestAsync<AudioRenditionResolveRequest, AudioRenditionResolveResponse>(
                AudioRenditionSubjects.Resolve,
                new AudioRenditionResolveRequest
                {
                    MediaGuid = mediaGuid,
                    StorageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey.Trim(),
                    SourceVersion = sourceVersion,
                    CreateIfMissing = createIfMissing
                },
                AudioQueryTimeout,
                cancellationToken);

            if (response is null)
                return (Unavailable(), null);

            if (!response.Success)
            {
                return (response.ErrorCode == "not_found"
                    ? new NotFoundObjectResult(response.ErrorMessage ?? "Audio rendition source was not found.")
                    : new ObjectResult(response.ErrorMessage ?? "Audio rendition lookup failed.")
                    {
                        StatusCode = StatusCodes.Status500InternalServerError
                    }, null);
            }

            return response.Item is null
                ? (new ObjectResult("DataBridge returned an invalid audio rendition response.")
                {
                    StatusCode = StatusCodes.Status502BadGateway
                }, null)
                : (null, response.Item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving audio rendition for {MediaGuid}.", mediaGuid);
            return (Unavailable(), null);
        }
    }

    private static ObjectResult Unavailable()
        => new("DataBridge is unreachable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
}

/// <summary>Content types and rendition storage-layout helpers. Audio renditions are opus-only.</summary>
public static class AudioRenditionHelpers
{
    /// <summary>Route token used in HLS segment URLs for the audio rendition (the codec is fixed).</summary>
    public const string FormatToken = "opus";

    public const string ContentType = "audio/ogg";

    public static bool IsSafeHlsFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            return false;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".ts" or ".m4s" or ".mp4";
    }

    public static string HlsFileContentType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".ts" => "video/mp2t",
            ".m4s" => "video/iso.segment",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

    public static string CombineStoragePath(string directory, string fileName)
        => string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory.TrimEnd('/')}/{fileName}";

    public static string HlsStorageDirectory(AudioRenditionDto rendition)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? "hls"
            : CombineStoragePath(StorageDirectory(rendition.StoragePath), "hls");

    public static string? HlsManifestStoragePath(AudioRenditionDto rendition)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? null
            : CombineStoragePath(HlsStorageDirectory(rendition), "index.m3u8");

    private static string StorageDirectory(string storagePath)
    {
        var slash = storagePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : storagePath[..slash];
    }
}
