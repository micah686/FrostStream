using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Common;
using WebAPI.Features.Playlists.Models;

namespace WebAPI.Features.Playlists.Controllers;

[ApiController]
[Route("api/user-playlists")]
public sealed class UserPlaylistsController(
    IMessageBus messageBus,
    ILogger<UserPlaylistsController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
    [Endpoint(EndpointIds.UserPlaylistsCreate)]
    [EndpointSummary("Create a user playlist")]
    [EndpointDescription("Creates a private playlist owned by the authenticated user. User playlists are separate from provider playlist metadata and are visible only to their owner.")]
    public async Task<ActionResult<UserPlaylistDto>> Create(
        [FromBody] UserPlaylistCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<UserPlaylistCreateRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistCreate,
            new UserPlaylistCreateRequestMessage
            {
                OwnerSubject = subject,
                Name = request.Name,
                Description = request.Description
            },
            "create user playlist",
            cancellationToken);

        return ToObjectResult(response, created: true);
    }

    [HttpGet]
    [Endpoint(EndpointIds.UserPlaylistsList)]
    [EndpointSummary("List user playlists")]
    [EndpointDescription("Lists private playlists owned by the authenticated user. Playlists owned by other users are never returned.")]
    public async Task<ActionResult<IReadOnlyList<UserPlaylistDto>>> List(
        [FromQuery] int pageSize = 50,
        [FromQuery] int pageOffset = 0,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        UserPlaylistListResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<UserPlaylistListRequestMessage, UserPlaylistListResponseMessage>(
                PlaylistSubjects.UserPlaylistList,
                new UserPlaylistListRequestMessage
                {
                    OwnerSubject = subject,
                    PageSize = pageSize,
                    PageOffset = pageOffset
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing user playlist list request for {Subject}.", subject);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");

        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        return Ok(response.Items ?? Array.Empty<UserPlaylistDto>());
    }

    [HttpGet("{playlistId:guid}")]
    [Endpoint(EndpointIds.UserPlaylistsGet)]
    [EndpointSummary("Get a user playlist")]
    [EndpointDescription("Gets one private playlist owned by the authenticated user, including ordered media GUIDs. Unknown playlists and playlists owned by another user return 404.")]
    public async Task<ActionResult<UserPlaylistDto>> Get(Guid playlistId, CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<UserPlaylistGetRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistGet,
            new UserPlaylistGetRequestMessage { OwnerSubject = subject, PlaylistId = playlistId },
            "get user playlist",
            cancellationToken);

        return ToObjectResult(response);
    }

    [HttpPatch("{playlistId:guid}")]
    [Endpoint(EndpointIds.UserPlaylistsUpdate)]
    [EndpointSummary("Update a user playlist")]
    [EndpointDescription("Updates the name and description of a private playlist owned by the authenticated user.")]
    public async Task<ActionResult<UserPlaylistDto>> Update(
        Guid playlistId,
        [FromBody] UserPlaylistUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<UserPlaylistUpdateRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistUpdate,
            new UserPlaylistUpdateRequestMessage
            {
                OwnerSubject = subject,
                PlaylistId = playlistId,
                Name = request.Name,
                Description = request.Description
            },
            "update user playlist",
            cancellationToken);

        return ToObjectResult(response);
    }

    [HttpDelete("{playlistId:guid}")]
    [Endpoint(EndpointIds.UserPlaylistsDelete)]
    [EndpointSummary("Delete a user playlist")]
    [EndpointDescription("Deletes a private playlist owned by the authenticated user. This does not delete the underlying media.")]
    public async Task<ActionResult> Delete(Guid playlistId, CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<UserPlaylistDeleteRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistDelete,
            new UserPlaylistDeleteRequestMessage { OwnerSubject = subject, PlaylistId = playlistId },
            "delete user playlist",
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");

        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        return NoContent();
    }

    [HttpPost("{playlistId:guid}/items")]
    [Endpoint(EndpointIds.UserPlaylistsAddItem)]
    [EndpointSummary("Add media to a user playlist")]
    [EndpointDescription("Adds an existing archived media item to a private playlist owned by the authenticated user.")]
    public async Task<ActionResult<UserPlaylistDto>> AddItem(
        Guid playlistId,
        [FromBody] UserPlaylistAddItemRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<UserPlaylistAddItemRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistAddItem,
            new UserPlaylistAddItemRequestMessage
            {
                OwnerSubject = subject,
                PlaylistId = playlistId,
                MediaGuid = request.MediaGuid,
                Position = request.Position
            },
            "add user playlist item",
            cancellationToken);

        return ToObjectResult(response);
    }

    [HttpDelete("{playlistId:guid}/items/{mediaGuid:guid}")]
    [Endpoint(EndpointIds.UserPlaylistsRemoveItem)]
    [EndpointSummary("Remove media from a user playlist")]
    [EndpointDescription("Removes a media item from a private playlist owned by the authenticated user. This does not delete the underlying media.")]
    public async Task<ActionResult<UserPlaylistDto>> RemoveItem(
        Guid playlistId,
        Guid mediaGuid,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<UserPlaylistRemoveItemRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistRemoveItem,
            new UserPlaylistRemoveItemRequestMessage
            {
                OwnerSubject = subject,
                PlaylistId = playlistId,
                MediaGuid = mediaGuid
            },
            "remove user playlist item",
            cancellationToken);

        return ToObjectResult(response);
    }

    [HttpPut("{playlistId:guid}/items/order")]
    [Endpoint(EndpointIds.UserPlaylistsReorderItems)]
    [EndpointSummary("Reorder a user playlist")]
    [EndpointDescription("Replaces the item order for a private playlist owned by the authenticated user. The request must contain every media GUID currently in the playlist exactly once.")]
    public async Task<ActionResult<UserPlaylistDto>> ReorderItems(
        Guid playlistId,
        [FromBody] UserPlaylistReorderRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<UserPlaylistReorderItemsRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistReorderItems,
            new UserPlaylistReorderItemsRequestMessage
            {
                OwnerSubject = subject,
                PlaylistId = playlistId,
                MediaGuids = request.MediaGuids
            },
            "reorder user playlist items",
            cancellationToken);

        return ToObjectResult(response);
    }

    private string? ResolveSubject()
        => AuthConstants.FindSubject(User);

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(
                subject,
                request,
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing {Operation}.", operation);
            return default;
        }
    }

    private ActionResult<UserPlaylistDto> ToObjectResult(UserPlaylistResponseMessage? response, bool created = false)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");

        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        if (response.Playlist is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid user playlist response.");

        return created ? CreatedAtAction(nameof(Get), new { playlistId = response.Playlist.PlaylistId }, response.Playlist) : Ok(response.Playlist);
    }

    private ObjectResult MapError(string? errorCode, string? errorMessage)
    {
        var message = errorMessage ?? "User playlist operation failed.";
        return errorCode switch
        {
            "not_found" => NotFound(message),
            "media_not_found" => NotFound(message),
            "duplicate" => Conflict(message),
            "invalid_order" => BadRequest(message),
            "validation" => BadRequest(message),
            _ => StatusCode(StatusCodes.Status500InternalServerError, message)
        };
    }
}
