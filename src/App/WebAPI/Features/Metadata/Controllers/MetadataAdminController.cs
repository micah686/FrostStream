using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Metadata.Controllers;

[ApiController]
[Route("api/global/metadata")]
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

    [HttpPost("database-reindex")]
    [Endpoint(EndpointIds.MetadataDatabaseReindex)]
    [EndpointSummary("Queue a whole-database concurrent reindex")]
    [EndpointDescription("Publishes an asynchronous background job that runs PostgreSQL REINDEX DATABASE CONCURRENTLY for the current database. The endpoint returns 202 once the request is accepted and does not wait for reindexing to finish.")]
    public async Task<IActionResult> TriggerDatabaseReindex(CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var request = BackgroundJobRequestFactory.CreateDatabaseMaintenanceReindex(
            BackgroundJobRequestFactory.ManualScheduleKey,
            BackgroundJobRequestFactory.ManualDatabaseMaintenanceReindexTaskType,
            now,
            now);

        try
        {
            await publisher.PublishAsync(
                BackgroundJobSubjects.DatabaseMaintenanceReindexRequest,
                request,
                request.IdempotencyKey,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing database reindex request.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to publish database reindex request.");
        }

        return Accepted();
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
            logger.LogError(ex, "Failed processing admin request on subject '{Subject}'.", subject);
            return default;
        }
    }

    private ObjectResult ServiceUnavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process admin request.");
}
