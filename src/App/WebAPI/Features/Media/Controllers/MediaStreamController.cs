using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Messaging;
using Shared.Storage;

namespace WebAPI.Features.Media.Controllers;

[ApiController]
[Route("stream")]
public sealed class MediaStreamController(
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    ILogger<MediaStreamController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    [HttpGet("{mediaGuid:guid}")]
    [EndpointSummary("Stream an archived media file")]
    [EndpointDescription("Resolves an archived media file by media GUID and streams it directly from the configured storage backend. Optional storageKey and positive version parameters select a specific stored copy; otherwise the latest matching version is used. Seekable streams support HTTP range requests for efficient playback, and the response content type is inferred from the stored file extension.")]
    public async Task<IActionResult> GetStream(
        Guid mediaGuid,
        [FromQuery] string? storageKey = null,
        [FromQuery] int? version = null,
        CancellationToken cancellationToken = default)
    {
        if (version is <= 0)
        {
            return BadRequest("Query parameter 'version' must be greater than zero.");
        }

        storageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey.Trim();

        MediaStreamResolveResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<
                MediaStreamResolveRequestMessage,
                MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                new MediaStreamResolveRequestMessage
                {
                    MediaGuid = mediaGuid,
                    StorageKey = storageKey,
                    Version = version
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving media stream for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (!response.Success)
        {
            return response.ErrorCode == "not_found"
                ? NotFound(response.ErrorMessage ?? "Media stream was not found.")
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    response.ErrorMessage ?? "Media stream lookup failed.");
        }

        if (response.Item is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "DataBridge returned an invalid media stream response.");
        }

        var location = response.Item;
        try
        {
            var storage = await blobStorageProvider.GetAsync(location.StorageKey, cancellationToken);
            var stream = await storage.OpenReadAsync(location.StoragePath, cancellationToken);
            if (stream is null)
            {
                return NotFound("The selected media stream is missing from storage.");
            }

            var contentType = ContentTypeProvider.TryGetContentType(location.StoragePath, out var resolvedContentType)
                ? resolvedContentType
                : "application/octet-stream";

            return File(stream, contentType, enableRangeProcessing: stream.CanSeek);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return NotFound("The selected media stream is missing from storage.");
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound("The selected media stream is missing from storage.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed opening media stream {MediaGuid} version {Version} from storage {StorageKey} at {StoragePath}.",
                location.MediaGuid,
                location.Version,
                location.StorageKey,
                location.StoragePath);

            return StatusCode(
                StatusCodes.Status502BadGateway,
                "The selected media stream could not be opened.");
        }
    }
}
