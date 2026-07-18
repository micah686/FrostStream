using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Common;
using WebAPI.Features.DownloadConfigSets;
using WebAPI.Features.Playlists.Models;

namespace WebAPI.Features.Playlists.Controllers;

[ApiController]
[Route("api/playlists")]
public class PlaylistsController(
    IJetStreamPublisher jetStreamPublisher,
    IMessageBus messageBus,
    IClock clock,
    ILogger<PlaylistsController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Submits a new playlist for download. Publishes <see cref="PlaylistRequested"/> to
    /// JetStream and returns immediately. Polling status uses <see cref="GetById"/>.
    /// </summary>
    [HttpPost]
    [Endpoint(EndpointIds.PlaylistsCreate)]
    [EndpointSummary("Queue a playlist download")]
    [EndpointDescription("Creates a playlist ingestion request and publishes it to the durable playlist stream. A blank storage key is normalized to the default storage target, and the response contains playlist and correlation identifiers for subsequent status queries.")]
    public async Task<ActionResult<PlaylistRequestResponse>> Submit(
        [FromBody] PlaylistRequest request,
        CancellationToken cancellationToken)
    {
        if (!YtDlpSourceUrlValidator.TryValidate(request.SourceUrl, out var validationError))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid source URL",
                Detail = validationError,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var playlistId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var subject = AuthConstants.FindSubject(User);
        ResolvedDownloadConfigSet resolved;
        try
        {
            var (config, error) = await DownloadConfigSetResolver.ResolveAsync(
                messageBus,
                subject,
                request.ConfigSetKey,
                request.StorageKey,
                request.CookieProfileKey,
                request.YtDlpOptions,
                request.EncodeForPlaylist,
                request.Priority,
                request.FetchComments,
                cancellationToken);
            if (error is not null)
                return BadRequest(error);
            resolved = config!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving playlist download config set {ConfigSetKey}.", request.ConfigSetKey);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to resolve download config set.");
        }

        var message = new PlaylistRequested
        {
            PlaylistId = playlistId,
            CorrelationId = correlationId,
            CausationId = null,
            MessageId = messageId,
            OperationKey = $"playlist/{playlistId:N}/requested",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            SourceUrl = request.SourceUrl,
            // Stamp the validated token subject, never client-supplied text, so "requested by" is trustworthy.
            RequestedBy = subject,
            StorageKey = resolved.StorageKey,
            ConfigSetKey = resolved.ConfigSetKey,
            EncodeForPlaylist = resolved.EncodeForPlaylist,
            CookieSecretPath = resolved.CookieSecretPath,
            YtDlpOptions = resolved.YtDlpOptions,
            Priority = resolved.Priority,
            FetchComments = resolved.FetchComments
        };

        try
        {
            await jetStreamPublisher.PublishAsync(
                PlaylistSubjects.PlaylistRequested,
                message,
                messageId: messageId.ToString("N"),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing PlaylistRequested for PlaylistId {PlaylistId}", playlistId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to submit playlist request",
                Detail = "Could not publish to the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        return Accepted(new PlaylistRequestResponse(playlistId, correlationId));
    }

    /// <summary>
    /// Lists all playlists, newest first. Does not include per-item details — use the
    /// <see cref="GetById"/> endpoint for that.
    /// </summary>
    [HttpGet]
    [Endpoint(EndpointIds.PlaylistsList)]
    [EndpointSummary("List playlist download requests")]
    [EndpointDescription("Returns playlist records in newest-first order using offset pagination. The list response omits the full per-item playlist contents; request a specific playlist identifier to retrieve its detailed item list and current processing state.")]
    public async Task<ActionResult<IReadOnlyList<PlaylistDto>>> List(
        [FromQuery] int pageSize = 50,
        [FromQuery] int pageOffset = 0,
        CancellationToken cancellationToken = default)
    {
        PlaylistListResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<PlaylistListRequestMessage, PlaylistListResponseMessage>(
                PlaylistSubjects.PlaylistList,
                new PlaylistListRequestMessage
                {
                    PageSize = pageSize,
                    PageOffset = pageOffset,
                    OwnerSubject = AuthConstants.FindSubject(User)
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing playlist list request");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");

        if (!response.Success)
            return StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Playlist list failed.");

        return Ok(response.Items ?? Array.Empty<PlaylistDto>());
    }

    /// <summary>
    /// Gets a single playlist by id, including the full item list.
    /// </summary>
    [HttpGet("{id:guid}")]
    [Endpoint(EndpointIds.PlaylistsGet)]
    [EndpointSummary("Get a playlist download request")]
    [EndpointDescription("Retrieves a playlist ingestion record by its identifier, including its current state and complete discovered item list. Returns 404 when the playlist is unknown and 503 when DataBridge cannot be reached within the request timeout.")]
    public async Task<ActionResult<PlaylistDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        PlaylistGetResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<PlaylistGetRequestMessage, PlaylistGetResponseMessage>(
                PlaylistSubjects.PlaylistGet,
                new PlaylistGetRequestMessage { PlaylistId = id, OwnerSubject = AuthConstants.FindSubject(User) },
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing playlist get request for PlaylistId {PlaylistId}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");

        if (!response.Success)
        {
            return response.ErrorCode switch
            {
                "not_found" => NotFound(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Playlist get failed.")
            };
        }

        if (response.Playlist is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid playlist response.");

        return Ok(response.Playlist);
    }

    /// <summary>
    /// Force-queues a playlist entry that was suppressed by an ignore keyword, re-downloading it with
    /// force enabled regardless of the config set's ignore list.
    /// </summary>
    [HttpPost("{id:guid}/items/{jobId:guid}/force-queue")]
    [Endpoint(EndpointIds.PlaylistsForceQueueItem)]
    [EndpointSummary("Force-queue an ignored playlist entry")]
    [EndpointDescription("Clears the ignored state of a playlist entry and re-queues it for download with force enabled, using the playlist's original download configuration. Returns 404 when the playlist or entry is unknown.")]
    public async Task<ActionResult<object>> ForceQueueItem(Guid id, Guid jobId, CancellationToken cancellationToken)
    {
        ForceQueueOperationResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<PlaylistItemForceQueueRequestMessage, ForceQueueOperationResponseMessage>(
                PlaylistSubjects.PlaylistItemForceQueue,
                new PlaylistItemForceQueueRequestMessage
                {
                    PlaylistId = id,
                    JobId = jobId,
                    RequestedBy = AuthConstants.FindSubject(User)
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed force-queueing playlist item {JobId} in {PlaylistId}", jobId, id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
        {
            return response.ErrorCode switch
            {
                "not_found" => NotFound(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Force-queue failed.")
            };
        }

        return Accepted(new { playlistId = id, jobId, queued = true });
    }
}
