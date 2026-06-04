using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;
using WebAPI.Features.Playlists.Models;

namespace WebAPI.Features.Playlists.Controllers;

[ApiController]
[Route("api/[controller]")]
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
    public async Task<ActionResult<PlaylistRequestResponse>> Submit(
        [FromBody] PlaylistRequest request,
        CancellationToken cancellationToken)
    {
        var playlistId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

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
            RequestedBy = request.RequestedBy,
            StorageKey = string.IsNullOrWhiteSpace(request.StorageKey) ? "default" : request.StorageKey
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
                new PlaylistListRequestMessage { PageSize = pageSize, PageOffset = pageOffset },
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
    public async Task<ActionResult<PlaylistDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        PlaylistGetResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<PlaylistGetRequestMessage, PlaylistGetResponseMessage>(
                PlaylistSubjects.PlaylistGet,
                new PlaylistGetRequestMessage { PlaylistId = id },
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
}
