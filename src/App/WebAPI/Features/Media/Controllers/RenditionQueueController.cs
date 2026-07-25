using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// Admin-facing, system-wide encoding queue surface: every stream (HLS) and audio (Opus) rendition
/// job regardless of what triggered it (channel bulk-encode, one-off watch/cast requests, etc.).
/// Progress is not included here because it is delivered live-only over the existing
/// <see cref="RenditionProgressController"/> SSE route, exactly like the download queue's split
/// between this read surface and its own SSE stream.
/// </summary>
[ApiController]
[Route("api/media/renditions/queue")]
public sealed class RenditionQueueController(IMessageBus messageBus, ILogger<RenditionQueueController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [HttpGet]
    [Endpoint(EndpointIds.MediaRenditionsQueueList)]
    [EndpointSummary("List the system-wide encoding queue")]
    [EndpointDescription("Returns a filtered, paginated slice of every stream and audio rendition row from the authoritative DataBridge read model, across the whole system. Supports filtering by kind, status, storage key, and a title/media-guid text query. Progress is not included because it is delivered live-only over the rendition progress SSE route.")]
    public async Task<ActionResult<RenditionQueueListResponse>> List(
        [FromQuery] RenditionKind? kind,
        [FromQuery] string? status,
        [FromQuery] string? storageKey,
        [FromQuery] string? q,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        RenditionQueueListResponse? response;
        try
        {
            response = await messageBus.RequestAsync<RenditionQueueListRequest, RenditionQueueListResponse>(
                RenditionQueueSubjects.List,
                new RenditionQueueListRequest
                {
                    Kind = kind,
                    Status = status,
                    StorageKey = storageKey,
                    Query = q,
                    Limit = limit,
                    Cursor = cursor
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing rendition queue list request.");
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "DataBridge is unreachable.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "DataBridge is unreachable.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        if (!response.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = response.ErrorMessage ?? "Rendition queue query failed.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        return Ok(response);
    }
}
