using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Metadata.Controllers;

[ApiController]
[Route("api/metadata")]
[Authorize(Policy = AuthPolicies.SystemManage)]
public sealed class MetadataAdminController(
    IJetStreamPublisher publisher,
    IMessageBus messageBus,
    IClock clock,
    ILogger<MetadataAdminController> logger) : ControllerBase
{
    private static readonly TimeSpan AdminRequestTimeout = TimeSpan.FromSeconds(30);

    [HttpPost("reindex")]
    [EndpointSummary("Queue a full metadata search reindex")]
    [EndpointDescription("Publishes an asynchronous background job that rebuilds the derived search index from authoritative metadata records. The endpoint returns 202 once the idempotent reindex request is accepted and does not wait for indexing to finish.")]
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
    [EndpointSummary("List orphan cleanup items")]
    [EndpointDescription("Returns paginated orphan-cleanup lifecycle records, optionally filtered by orphan kind and state. Results describe missing metadata or unexpected files detected by filesystem reconciliation and include the information needed to review restoration or cleanup actions.")]
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
    [EndpointSummary("Restore an orphaned file")]
    [EndpointDescription("Requests restoration of a file orphan by moving the archived object from its orphaned storage path back to its recorded original path. The operation validates lifecycle state and destination conflicts, returning 404 for an unknown item and 409 when restoration is no longer valid.")]
    public async Task<IActionResult> RestoreFile(long id, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<RestoreOrphanRequest, RestoreOrphanResponse>(
            OrphanCleanupSubjects.AdminRestoreFile,
            new RestoreOrphanRequest { OrphanId = id },
            cancellationToken);

        return MapRestoreResponse(response);
    }

    [HttpPost("orphans/{id:long}/restore-metadata")]
    [EndpointSummary("Restore orphaned metadata")]
    [EndpointDescription("Requests restoration of metadata previously marked orphaned after its expected file disappeared. Restoration succeeds only when the referenced content-version record remains valid and the expected storage object is present; invalid lifecycle states return 409.")]
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
