using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

[ApiController]
[Route("api/media/{mediaGuid:guid}/watch-state")]
public sealed class MediaWatchStateController(
    IMessageBus messageBus,
    ILogger<MediaWatchStateController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpGet]
    [Endpoint(EndpointIds.MediaWatchStateGet)]
    [EndpointSummary("Get the caller's watch state for a video")]
    [EndpointDescription("Returns the authenticated caller's playback/watch state for the supplied media item. A missing state returns 404; the state is scoped to the caller and cannot reveal another user's watch history.")]
    public async Task<IActionResult> Get(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var subject = AuthConstants.FindSubject(User);
        if (string.IsNullOrWhiteSpace(subject))
            return Unauthorized();

        var response = await SendAsync<WatchStateGetRequest, WatchStateResponse>(
            WatchStateSubjects.Get,
            new WatchStateGetRequest { OwnerSubject = subject, MediaGuid = mediaGuid },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to get watch state.");
        if (!response.Success)
            return MapWatchStateError(response);
        if (response.State is null)
        {
            return Ok(new WatchStateDto
            {
                OwnerSubject = subject,
                MediaGuid = mediaGuid,
                PositionSeconds = null,
                DurationSeconds = null,
                Completed = false,
                WatchedAt = null,
                LastPlayedAt = NodaTime.Instant.FromUtc(1970, 1, 1, 0, 0),
                UpdatedAt = NodaTime.Instant.FromUtc(1970, 1, 1, 0, 0)
            });
        }

        return Ok(response.State);
    }

    [HttpPut]
    [Endpoint(EndpointIds.MediaWatchStateUpsert)]
    [EndpointSummary("Update the caller's watch state for a video")]
    [EndpointDescription("Records playback progress for the authenticated caller and optionally marks the media item as completed. Completed watch states can later be selected by the admin-controlled watched-item auto-delete policy.")]
    public async Task<IActionResult> Upsert(
        Guid mediaGuid,
        [FromBody] WatchStateUpdateRequest request,
        CancellationToken cancellationToken)
        => await UpsertForCurrentUserAsync(
            mediaGuid,
            request.PositionSeconds,
            request.DurationSeconds,
            request.Completed,
            cancellationToken);

    [HttpPost("watched")]
    [Endpoint(EndpointIds.MediaWatchStateMarkWatched)]
    [EndpointSummary("Mark the caller's media item as watched")]
    [EndpointDescription("Marks the supplied media item as watched for the authenticated caller only. The operation records a completed watch state for that user and does not expose or modify another user's watch history.")]
    public async Task<IActionResult> MarkWatched(Guid mediaGuid, CancellationToken cancellationToken)
        => await UpsertForCurrentUserAsync(
            mediaGuid,
            positionSeconds: null,
            durationSeconds: null,
            completed: true,
            cancellationToken);

    [HttpPost("unwatched")]
    [Endpoint(EndpointIds.MediaWatchStateMarkUnwatched)]
    [EndpointSummary("Mark the caller's media item as unwatched")]
    [EndpointDescription("Marks the supplied media item as unwatched for the authenticated caller only. The operation clears that user's completed flag and watched timestamp without revealing or changing another user's watch history.")]
    public async Task<IActionResult> MarkUnwatched(Guid mediaGuid, CancellationToken cancellationToken)
        => await UpsertForCurrentUserAsync(
            mediaGuid,
            positionSeconds: null,
            durationSeconds: null,
            completed: false,
            cancellationToken);

    // Absolute route: this is a cross-media collection query, so it escapes the controller's
    // per-media `api/media/{mediaGuid}/watch-state` template.
    [HttpGet("/api/media/watch-states/in-progress")]
    [Endpoint(EndpointIds.MediaWatchStateListInProgress)]
    [EndpointSummary("List the caller's in-progress videos")]
    [EndpointDescription("Returns the authenticated caller's partially watched media items (playback started but not completed), newest first by last play time. Results are scoped to the caller and never include another user's watch history.")]
    public async Task<IActionResult> ListInProgress(
        [FromQuery] int limit = 12,
        CancellationToken cancellationToken = default)
    {
        var subject = AuthConstants.FindSubject(User);
        if (string.IsNullOrWhiteSpace(subject))
            return Unauthorized();

        var response = await SendAsync<WatchStateInProgressListRequest, WatchStateListResponse>(
            WatchStateSubjects.ListInProgress,
            new WatchStateInProgressListRequest { OwnerSubject = subject, Limit = limit },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to list in-progress watch states.");

        if (!response.Success)
        {
            return response.ErrorCode switch
            {
                "validation" => BadRequest(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Watch-state request failed.")
            };
        }

        return Ok(response.Items ?? Array.Empty<WatchStateDto>());
    }

    // Absolute route: this is a cross-media collection query, so it escapes the controller's
    // per-media `api/media/{mediaGuid}/watch-state` template.
    [HttpGet("/api/media/watch-states/history")]
    [Endpoint(EndpointIds.MediaWatchStateListHistory)]
    [EndpointSummary("List the caller's watch history")]
    [EndpointDescription("Returns the authenticated caller's watch history, newest first by last play time. Results include media cards and are scoped to the caller.")]
    public async Task<IActionResult> ListHistory(
        [FromQuery] int pageSize = 24,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        var subject = AuthConstants.FindSubject(User);
        if (string.IsNullOrWhiteSpace(subject))
            return Unauthorized();

        var response = await SendAsync<WatchStateHistoryListRequest, WatchStateHistoryListResponse>(
            WatchStateSubjects.ListHistory,
            new WatchStateHistoryListRequest
            {
                OwnerSubject = subject,
                PageSize = pageSize,
                Page = page
            },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to list watch history.");

        if (!response.Success)
        {
            return response.ErrorCode switch
            {
                "validation" => BadRequest(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Watch-history request failed.")
            };
        }

        return Ok(response);
    }

    private async Task<IActionResult> UpsertForCurrentUserAsync(
        Guid mediaGuid,
        double? positionSeconds,
        double? durationSeconds,
        bool completed,
        CancellationToken cancellationToken)
    {
        var subject = AuthConstants.FindSubject(User);
        if (string.IsNullOrWhiteSpace(subject))
            return Unauthorized();

        var response = await SendAsync<WatchStateUpsertRequest, WatchStateResponse>(
            WatchStateSubjects.Upsert,
            new WatchStateUpsertRequest
            {
                OwnerSubject = subject,
                MediaGuid = mediaGuid,
                PositionSeconds = positionSeconds,
                DurationSeconds = durationSeconds,
                Completed = completed
            },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to update watch state.");
        if (!response.Success)
            return MapWatchStateError(response);

        return Ok(response.State);
    }

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing watch-state request on subject '{Subject}'.", subject);
            return default;
        }
    }

    private IActionResult MapWatchStateError(WatchStateResponse response)
        => response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Watch-state request failed.")
        };
}

public sealed record WatchStateUpdateRequest
{
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool Completed { get; init; }
}
