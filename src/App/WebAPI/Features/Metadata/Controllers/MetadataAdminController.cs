using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;
using WebAPI.Auth;

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
    [Endpoint(EndpointIds.MetadataReindex)]
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
    [Endpoint(EndpointIds.MetadataOrphansList)]
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
    [Endpoint(EndpointIds.MetadataOrphansRestoreFile)]
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
    [Endpoint(EndpointIds.MetadataOrphansRestoreMetadata)]
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

    [HttpGet("orphan-cleanup-policy")]
    [Endpoint(EndpointIds.OrphanCleanupPolicyGet)]
    [EndpointSummary("Get orphan-cleanup policy")]
    [EndpointDescription("Returns the global orphan-cleanup policy: whether automatic cleanup is enabled, the delay before a file with no metadata is moved into the orphaned folder, the delay before a moved file is permanently purged, the delay before metadata whose file is missing is deleted, and the latest run counters.")]
    public async Task<IActionResult> GetOrphanCleanupPolicy(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<OrphanCleanupPolicyGetRequest, OrphanCleanupPolicyResponse>(
            OrphanCleanupSubjects.AdminGetPolicy,
            new OrphanCleanupPolicyGetRequest(),
            cancellationToken);

        return MapOrphanPolicyResponse(response);
    }

    [HttpPut("orphan-cleanup-policy")]
    [Endpoint(EndpointIds.OrphanCleanupPolicyUpdate)]
    [EndpointSummary("Update orphan-cleanup policy")]
    [EndpointDescription("Enables or disables automatic orphan cleanup and sets its three retention timers (in days): how long before a file with no metadata is moved to the orphaned folder, how long a moved file is retained there before permanent deletion, and how long before metadata with a missing file is deleted. Destructive cleanup runs only while this admin-controlled policy is enabled.")]
    public async Task<IActionResult> UpdateOrphanCleanupPolicy(
        [FromBody] OrphanCleanupPolicyUpdateHttpRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<OrphanCleanupPolicyUpdateRequest, OrphanCleanupPolicyResponse>(
            OrphanCleanupSubjects.AdminUpdatePolicy,
            new OrphanCleanupPolicyUpdateRequest
            {
                Enabled = request.Enabled,
                FileMoveAfterDays = request.FileMoveAfterDays,
                FilePurgeAfterDays = request.FilePurgeAfterDays,
                MetadataDeleteAfterDays = request.MetadataDeleteAfterDays,
                UpdatedBy = Shared.Auth.AuthConstants.FindSubject(User)
            },
            cancellationToken);

        return MapOrphanPolicyResponse(response);
    }

    [HttpDelete("{mediaGuid:guid}")]
    [Endpoint(EndpointIds.MediaDelete)]
    [EndpointSummary("Delete a video globally")]
    [EndpointDescription("Permanently deletes a video and every copy of it: all stored objects across every storage key (video files, thumbnails, and caption sidecars), the authoritative metadata record, and its derived search-index entries. Returns 404 for an unknown video and 409 when an active download job is still in flight for it.")]
    public async Task<IActionResult> DeleteMedia(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MediaDeleteRequest, MediaDeleteResponse>(
            MediaDeleteSubjects.Delete,
            new MediaDeleteRequest { MediaGuid = mediaGuid },
            cancellationToken);

        return MapDeleteResponse(response);
    }

    [HttpDelete("{mediaGuid:guid}/storage/{storageKey}")]
    [Endpoint(EndpointIds.MediaDeleteForStorageKey)]
    [EndpointSummary("Delete a video's copy on one storage key")]
    [EndpointDescription("Deletes a video's stored copy on a single storage backend: its content file plus any thumbnail and caption sidecars held on that key. When the key holds the last remaining copy, the operation cascades to a full delete (metadata and search entries are also removed). Returns 404 when the video or the storage-key copy is unknown and 409 when an active download job is in flight.")]
    public async Task<IActionResult> DeleteMediaForStorageKey(Guid mediaGuid, string storageKey, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MediaDeleteForStorageKeyRequest, MediaDeleteResponse>(
            MediaDeleteSubjects.DeleteForStorageKey,
            new MediaDeleteForStorageKeyRequest { MediaGuid = mediaGuid, StorageKey = storageKey },
            cancellationToken);

        return MapDeleteResponse(response);
    }

    [HttpGet("watched-auto-delete")]
    [Endpoint(EndpointIds.WatchedAutoDeletePolicyGet)]
    [EndpointSummary("Get watched-item auto-delete policy")]
    [EndpointDescription("Returns the global watched-item auto-delete policy, including whether it is enabled, its retention period, batch limit, and latest run counters. This policy controls destructive automatic deletion and is intentionally separate from reader playback endpoints.")]
    public async Task<IActionResult> GetWatchedAutoDeletePolicy(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<WatchedAutoDeletePolicyGetRequest, WatchedAutoDeletePolicyResponse>(
            WatchedAutoDeleteSubjects.GetPolicy,
            new WatchedAutoDeletePolicyGetRequest(),
            cancellationToken);

        return MapPolicyResponse(response);
    }

    [HttpPut("watched-auto-delete")]
    [Endpoint(EndpointIds.WatchedAutoDeletePolicyUpdate)]
    [EndpointSummary("Update watched-item auto-delete policy")]
    [EndpointDescription("Enables or disables automatic deletion of watched media and sets the retention period and maximum deletions per cleanup run. The scheduled cleanup only deletes media after this admin-controlled policy is enabled.")]
    public async Task<IActionResult> UpdateWatchedAutoDeletePolicy(
        [FromBody] WatchedAutoDeletePolicyUpdateHttpRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<WatchedAutoDeletePolicyUpdateRequest, WatchedAutoDeletePolicyResponse>(
            WatchedAutoDeleteSubjects.UpdatePolicy,
            new WatchedAutoDeletePolicyUpdateRequest
            {
                Enabled = request.Enabled,
                DeleteAfterDays = request.DeleteAfterDays,
                MaxDeletionsPerRun = request.MaxDeletionsPerRun,
                UpdatedBy = Shared.Auth.AuthConstants.FindSubject(User)
            },
            cancellationToken);

        return MapPolicyResponse(response);
    }

    [HttpPost("watched-auto-delete/run")]
    [Endpoint(EndpointIds.WatchedAutoDeleteRun)]
    [EndpointSummary("Run watched-item auto-delete cleanup")]
    [EndpointDescription("Runs the watched-item auto-delete cleanup immediately using the persisted global policy. The operation uses the same deletion executor as manual media deletion, removing stored files, metadata, and search index entries for expired watched items.")]
    public async Task<IActionResult> RunWatchedAutoDelete(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<WatchedAutoDeleteCleanupRunRequest, WatchedAutoDeleteCleanupResponse>(
            WatchedAutoDeleteSubjects.RunCleanup,
            new WatchedAutoDeleteCleanupRunRequest(),
            cancellationToken);

        if (response is null)
        {
            return ServiceUnavailable();
        }

        if (response.Success)
        {
            return Ok(response.Result);
        }

        return response.ErrorCode switch
        {
            "validation" => BadRequest(response.ErrorMessage),
            "unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Watched auto-delete cleanup failed.")
        };
    }

    private IActionResult MapDeleteResponse(MediaDeleteResponse? response)
    {
        if (response is null)
        {
            return ServiceUnavailable();
        }

        if (response.Success)
        {
            return Ok(response);
        }

        return response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "conflict" => Conflict(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            "unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Media delete request failed.")
        };
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

    private IActionResult MapOrphanPolicyResponse(OrphanCleanupPolicyResponse? response)
    {
        if (response is null)
        {
            return ServiceUnavailable();
        }

        if (response.Success)
        {
            return Ok(response.Policy);
        }

        return response.ErrorCode switch
        {
            "validation" => BadRequest(response.ErrorMessage),
            "unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Orphan cleanup policy request failed.")
        };
    }

    private IActionResult MapPolicyResponse(WatchedAutoDeletePolicyResponse? response)
    {
        if (response is null)
        {
            return ServiceUnavailable();
        }

        if (response.Success)
        {
            return Ok(response.Policy);
        }

        return response.ErrorCode switch
        {
            "validation" => BadRequest(response.ErrorMessage),
            "unavailable" => StatusCode(StatusCodes.Status503ServiceUnavailable, response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Watched auto-delete policy request failed.")
        };
    }
}

public sealed record WatchedAutoDeletePolicyUpdateHttpRequest
{
    public required bool Enabled { get; init; }
    public required int DeleteAfterDays { get; init; }
    public int MaxDeletionsPerRun { get; init; } = 100;
}

public sealed record OrphanCleanupPolicyUpdateHttpRequest
{
    public required bool Enabled { get; init; }
    public required int FileMoveAfterDays { get; init; }
    public required int FilePurgeAfterDays { get; init; }
    public required int MetadataDeleteAfterDays { get; init; }
}
