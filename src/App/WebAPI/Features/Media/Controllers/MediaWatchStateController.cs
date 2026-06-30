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
            return NotFound();

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
