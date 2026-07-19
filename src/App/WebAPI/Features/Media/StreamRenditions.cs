using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;

namespace WebAPI.Features.Media;

/// <summary>
/// Resolves (and optionally queues) the cached H.264/AAC HLS stream rendition of a media item
/// through DataBridge. Shared by the HLS manifest/segment endpoints and cast session start.
/// </summary>
public sealed class StreamRenditionResolver(IMessageBus messageBus, ILogger<StreamRenditionResolver> logger)
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    public async Task<(IActionResult? Error, StreamRenditionDto? Rendition)> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await messageBus.RequestAsync<StreamRenditionResolveRequest, StreamRenditionResolveResponse>(
                StreamRenditionSubjects.Resolve,
                new StreamRenditionResolveRequest
                {
                    MediaGuid = mediaGuid,
                    StorageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey.Trim(),
                    SourceVersion = sourceVersion,
                    CreateIfMissing = createIfMissing
                },
                QueryTimeout,
                cancellationToken);

            if (response is null)
                return (Unavailable(), null);

            if (!response.Success)
            {
                return (response.ErrorCode == "not_found"
                    ? new NotFoundObjectResult(response.ErrorMessage ?? "Stream rendition source was not found.")
                    : new ObjectResult(response.ErrorMessage ?? "Stream rendition lookup failed.")
                    {
                        StatusCode = StatusCodes.Status500InternalServerError
                    }, null);
            }

            return response.Item is null
                ? (new ObjectResult("DataBridge returned an invalid stream rendition response.")
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
            logger.LogError(ex, "Failed resolving stream rendition for {MediaGuid}.", mediaGuid);
            return (Unavailable(), null);
        }
    }

    private static ObjectResult Unavailable()
        => new("DataBridge is unreachable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
}

/// <summary>Storage-layout helpers for video HLS renditions.</summary>
public static class StreamRenditionHelpers
{
    /// <summary>Route token used in HLS segment URLs for the video rendition (the codec is fixed).</summary>
    public const string FormatToken = "h264";

    public const string HlsContentType = "application/vnd.apple.mpegurl";

    public static string? HlsManifestStoragePath(StreamRenditionDto rendition)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? null
            : $"{rendition.StoragePath.TrimEnd('/')}/index.m3u8";

    public static string? SegmentStoragePath(StreamRenditionDto rendition, string fileName)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? null
            : $"{rendition.StoragePath.TrimEnd('/')}/{fileName}";
}
