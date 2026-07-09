using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

[ApiController]
[Route("api/media")]
public sealed class MediaLikesController(
    IMessageBus messageBus,
    ILogger<MediaLikesController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpGet("{mediaGuid:guid}/like-state")]
    [Endpoint(EndpointIds.MediaLikeStateGet)]
    [EndpointSummary("Get the caller's like state for a video")]
    [EndpointDescription("Returns whether the authenticated caller has liked the supplied media item. The state is private to the caller and does not expose or aggregate other users' reactions.")]
    public async Task<IActionResult> Get(Guid mediaGuid, CancellationToken cancellationToken)
        => await SendStateRequestAsync(MediaLikeSubjects.Get, mediaGuid, cancellationToken);

    [HttpPost("{mediaGuid:guid}/like")]
    [Endpoint(EndpointIds.MediaLike)]
    [EndpointSummary("Like a video")]
    [EndpointDescription("Marks the supplied media item as liked for the authenticated caller only. The operation creates or refreshes that user's private like without changing provider-scraped like counts.")]
    public async Task<IActionResult> Like(Guid mediaGuid, CancellationToken cancellationToken)
        => await SendStateRequestAsync(MediaLikeSubjects.Like, mediaGuid, cancellationToken);

    [HttpDelete("{mediaGuid:guid}/like")]
    [Endpoint(EndpointIds.MediaUnlike)]
    [EndpointSummary("Unlike a video")]
    [EndpointDescription("Clears the authenticated caller's private like for the supplied media item. Other users' likes and the archived provider metadata remain unchanged.")]
    public async Task<IActionResult> Unlike(Guid mediaGuid, CancellationToken cancellationToken)
        => await SendStateRequestAsync(MediaLikeSubjects.Unlike, mediaGuid, cancellationToken);

    [HttpGet("likes")]
    [Endpoint(EndpointIds.MediaLikesList)]
    [EndpointSummary("List the caller's liked videos")]
    [EndpointDescription("Returns the authenticated caller's liked media, newest first by like time. Results are private to the caller.")]
    public async Task<IActionResult> List(
        [FromQuery] int pageSize = 24,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        var subject = AuthConstants.FindSubject(User);
        if (string.IsNullOrWhiteSpace(subject))
            return Unauthorized();

        var response = await SendAsync<MediaLikeListRequest, MediaLikeListResponse>(
            MediaLikeSubjects.List,
            new MediaLikeListRequest
            {
                OwnerSubject = subject,
                PageSize = pageSize,
                Page = page
            },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to list liked media.");

        if (!response.Success)
        {
            return response.ErrorCode switch
            {
                "validation" => BadRequest(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Like request failed.")
            };
        }

        return Ok(response);
    }

    private async Task<IActionResult> SendStateRequestAsync(
        string subjectName,
        Guid mediaGuid,
        CancellationToken cancellationToken)
    {
        var subject = AuthConstants.FindSubject(User);
        if (string.IsNullOrWhiteSpace(subject))
            return Unauthorized();

        var response = await SendAsync<MediaLikeStateRequest, MediaLikeStateResponse>(
            subjectName,
            new MediaLikeStateRequest { OwnerSubject = subject, MediaGuid = mediaGuid },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to update like state.");

        if (!response.Success)
        {
            return response.ErrorCode switch
            {
                "not_found" => NotFound(response.ErrorMessage),
                "validation" => BadRequest(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Like request failed.")
            };
        }

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
            logger.LogError(ex, "Failed processing media-like request on subject '{Subject}'.", subject);
            return default;
        }
    }
}
