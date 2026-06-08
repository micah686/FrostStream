using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Messaging;
using Shared.Storage;

namespace WebAPI.Features.Media.Controllers;

[ApiController]
[Route("api/media")]
public sealed class MediaContentController(
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    ILogger<MediaContentController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    [HttpGet("{mediaGuid:guid}/content")]
    public async Task<IActionResult> GetContent(
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

        MediaContentResolveResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<
                MediaContentResolveRequestMessage,
                MediaContentResolveResponseMessage>(
                MediaContentSubjects.Resolve,
                new MediaContentResolveRequestMessage
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
            logger.LogError(ex, "Failed resolving media content for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (!response.Success)
        {
            return response.ErrorCode == "not_found"
                ? NotFound(response.ErrorMessage ?? "Media content was not found.")
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    response.ErrorMessage ?? "Media content lookup failed.");
        }

        if (response.Item is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "DataBridge returned an invalid media content response.");
        }

        var location = response.Item;
        try
        {
            var storage = await blobStorageProvider.GetAsync(location.StorageKey, cancellationToken);
            var stream = await storage.OpenReadAsync(location.StoragePath, cancellationToken);
            if (stream is null)
            {
                return NotFound("The selected media content is missing from storage.");
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
            return NotFound("The selected media content is missing from storage.");
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound("The selected media content is missing from storage.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed opening media content {MediaGuid} version {Version} from storage {StorageKey} at {StoragePath}.",
                location.MediaGuid,
                location.Version,
                location.StorageKey,
                location.StoragePath);

            return StatusCode(
                StatusCodes.Status502BadGateway,
                "The selected media content could not be opened.");
        }
    }
}
