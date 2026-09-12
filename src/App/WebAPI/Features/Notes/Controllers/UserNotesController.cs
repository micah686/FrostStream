using Microsoft.AspNetCore.Mvc;
using Shared.Application;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Notes.Models;

namespace WebAPI.Features.Notes.Controllers;

[ApiController]
[Route("api/user/notes")]
public sealed class UserNotesController(
    IUserNoteApplication application,
    ICurrentOwner currentOwner) : ControllerBase
{
    [HttpPut("{targetType}/{targetId}")]
    [Endpoint(EndpointIds.UserNotesUpsert)]
    [EndpointSummary("Create or update a user note")]
    [EndpointDescription("Creates or replaces the authenticated user's private note for a video, channel, or playlist target. Target identifiers are media GUIDs for videos, account ids for channels, and playlist GUIDs for playlists.")]
    public async Task<ActionResult<UserNoteDto>> Upsert(
        string targetType,
        string targetId,
        [FromBody] UserNoteUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } owner)
            return Unauthorized();

        var response = await application.UpsertAsync(
            new UserNoteUpsertRequestMessage
            {
                OwnerSubject = owner,
                TargetType = targetType,
                TargetId = targetId,
                Note = request.Note
            },
            cancellationToken);

        return ToObjectResult(response);
    }

    [HttpGet("{targetType}/{targetId}")]
    [Endpoint(EndpointIds.UserNotesGet)]
    [EndpointSummary("Get a user note")]
    [EndpointDescription("Gets the authenticated user's private note for a video, channel, or playlist target. Notes owned by other users are never returned. Returns a JSON null body when the user has no note for the target.")]
    public async Task<ActionResult<UserNoteDto>> Get(
        string targetType,
        string targetId,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } owner)
            return Unauthorized();

        var response = await application.GetAsync(
            new UserNoteGetRequestMessage
            {
                OwnerSubject = owner,
                TargetType = targetType,
                TargetId = targetId
            },
            cancellationToken);

        // Having no note for a target is a normal empty result, not an error: answer 200 with a
        // JSON null so clients don't have to treat 404 as "no note yet". JsonResult is used
        // instead of Ok(null) because null object results are otherwise rewritten to 204, whose
        // empty body breaks response.json() callers. A missing target still maps to 404 below.
        if (response is { Success: false, ErrorCode: "not_found" })
            return new JsonResult(null);

        return ToObjectResult(response);
    }

    [HttpDelete("{targetType}/{targetId}")]
    [Endpoint(EndpointIds.UserNotesDelete)]
    [EndpointSummary("Delete a user note")]
    [EndpointDescription("Deletes the authenticated user's private note for a video, channel, or playlist target. The underlying target is not modified. Deleting is idempotent: a target with no note still answers 204.")]
    public async Task<ActionResult> Delete(
        string targetType,
        string targetId,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } owner)
            return Unauthorized();

        var response = await application.DeleteAsync(
            new UserNoteDeleteRequestMessage
            {
                OwnerSubject = owner,
                TargetType = targetType,
                TargetId = targetId
            },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");

        // Idempotent delete: the desired end state (no note) already holds, so a missing note is
        // success, not 404 — mirroring Get's empty-result handling.
        if (!response.Success && response.ErrorCode != "not_found")
            return MapError(response.ErrorCode, response.ErrorMessage);

        return NoContent();
    }

    [HttpGet("search")]
    [Endpoint(EndpointIds.UserNotesSearch)]
    [EndpointSummary("Search user notes")]
    [EndpointDescription("Searches the authenticated user's private notes across videos, channels, and playlists. The optional targetType filter accepts video, channel, or playlist.")]
    public async Task<ActionResult<UserNoteSearchResponseMessage>> Search(
        [FromQuery(Name = "q")] string q,
        [FromQuery] string? targetType = null,
        [FromQuery] int pageSize = 50,
        [FromQuery] int pageOffset = 0,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSubject() is not { } owner)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("Query parameter 'q' is required.");

        var response = await application.SearchAsync(
            new UserNoteSearchRequestMessage
            {
                OwnerSubject = owner,
                Query = q,
                TargetType = targetType,
                PageSize = pageSize,
                PageOffset = pageOffset
            },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpGet]
    [Endpoint(EndpointIds.UserNotesList)]
    [EndpointSummary("List user notes")]
    [EndpointDescription("Lists the authenticated user's private notes across videos, channels, and playlists. The optional targetType filter accepts video, channel, or playlist.")]
    public async Task<ActionResult<UserNoteSearchResponseMessage>> List(
        [FromQuery] string? targetType = null,
        [FromQuery] int pageSize = 50,
        [FromQuery] int pageOffset = 0,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSubject() is not { } owner)
            return Unauthorized();

        var response = await application.SearchAsync(
            new UserNoteSearchRequestMessage
            {
                OwnerSubject = owner,
                Query = string.Empty,
                TargetType = targetType,
                PageSize = pageSize,
                PageOffset = pageOffset
            },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    private string? ResolveSubject()
        => currentOwner.Subject;

    private ActionResult<UserNoteDto> ToObjectResult(UserNoteResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);
        if (response.Note is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid user note response.");

        return Ok(response.Note);
    }

    private ObjectResult MapError(string? errorCode, string? errorMessage)
    {
        var message = errorMessage ?? "User note operation failed.";
        return errorCode switch
        {
            "not_found" => NotFound(message),
            "target_not_found" => NotFound(message),
            "validation" => BadRequest(message),
            _ => StatusCode(StatusCodes.Status500InternalServerError, message)
        };
    }
}
