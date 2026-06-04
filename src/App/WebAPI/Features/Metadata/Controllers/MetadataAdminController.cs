using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;

namespace WebAPI.Features.Metadata.Controllers;

[ApiController]
[Route("api/metadata")]
public sealed class MetadataAdminController(
    IJetStreamPublisher publisher,
    IMessageBus messageBus,
    IClock clock,
    ILogger<MetadataAdminController> logger) : ControllerBase
{
    private static readonly TimeSpan AdminRequestTimeout = TimeSpan.FromSeconds(30);

    [HttpPost("reindex")]
    public async Task<IActionResult> TriggerReindex(CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var request = BackgroundJobRequestFactory.CreateSearchReindex(
            BackgroundJobRequestFactory.ManualScheduleKey,
            BackgroundJobRequestFactory.ManualSearchReindexTaskType,
            now,
            now);

        try
        {
            await publisher.PublishAsync(
                BackgroundJobSubjects.SearchReindexRequest,
                request,
                request.IdempotencyKey,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing metadata reindex request.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to publish metadata reindex request.");
        }

        return Accepted();
    }

    [HttpGet("orphans")]
    public async Task<ActionResult<IReadOnlyList<OrphanCleanupItemDto>>> ListOrphans(
        [FromQuery] string? kind = null,
        [FromQuery] string? state = null,
        [FromQuery] int pageSize = 100,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<OrphanCleanupListRequest, OrphanCleanupListResponse>(
            OrphanCleanupSubjects.AdminList,
            new OrphanCleanupListRequest
            {
                Kind = kind,
                State = state,
                PageSize = pageSize,
                Page = page
            },
            cancellationToken);

        if (response is null)
        {
            return ServiceUnavailable();
        }

        if (!response.Success)
        {
            return MapListError(response);
        }

        return Ok(response.Items);
    }

    [HttpPost("orphans/{id:long}/restore-file")]
    public async Task<IActionResult> RestoreFile(long id, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<RestoreOrphanRequest, RestoreOrphanResponse>(
            OrphanCleanupSubjects.AdminRestoreFile,
            new RestoreOrphanRequest { OrphanId = id },
            cancellationToken);

        return MapRestoreResponse(response);
    }

    [HttpPost("orphans/{id:long}/restore-metadata")]
    public async Task<IActionResult> RestoreMetadata(long id, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<RestoreOrphanRequest, RestoreOrphanResponse>(
            OrphanCleanupSubjects.AdminRestoreMetadata,
            new RestoreOrphanRequest { OrphanId = id },
            cancellationToken);

        return MapRestoreResponse(response);
    }

    private async Task<TResponse?> SendRequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(
                subject,
                request,
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing orphan cleanup admin request on subject '{Subject}'.", subject);
            return default;
        }
    }

    private ActionResult<IReadOnlyList<OrphanCleanupItemDto>> MapListError(OrphanCleanupListResponse response)
        => response.ErrorCode switch
        {
            "validation" => BadRequest(response.ErrorMessage),
            "unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Orphan cleanup request failed.")
        };

    private IActionResult MapRestoreResponse(RestoreOrphanResponse? response)
    {
        if (response is null)
        {
            return ServiceUnavailable();
        }

        if (response.Success)
        {
            return Ok();
        }

        return response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "conflict" => Conflict(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            "unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Orphan cleanup request failed.")
        };
    }

    private ObjectResult ServiceUnavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process orphan cleanup admin request.");
}
