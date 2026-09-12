using Microsoft.AspNetCore.Mvc;
using Shared.Application;
using Shared.Messaging;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/user/notes")]
public sealed class UserNotesController(
    IUserNoteApplication application,
    ICurrentOwner currentOwner) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? targetType = null,
        [FromQuery] int pageSize = 50,
        [FromQuery] int pageOffset = 0,
        CancellationToken cancellationToken = default)
    {
        var response = await application.SearchAsync(new UserNoteSearchRequestMessage
        {
            OwnerSubject = Owner,
            Query = string.Empty,
            TargetType = targetType,
            PageSize = pageSize,
            PageOffset = pageOffset
        }, cancellationToken);
        return Map(response);
    }

    [HttpGet("{targetType}/{targetId}")]
    public async Task<IActionResult> Get(
        string targetType,
        string targetId,
        CancellationToken cancellationToken)
    {
        var response = await application.GetAsync(new UserNoteGetRequestMessage
        {
            OwnerSubject = Owner,
            TargetType = targetType,
            TargetId = targetId
        }, cancellationToken);
        return response is { Success: false, ErrorCode: "not_found" }
            ? new JsonResult(null)
            : Map(response);
    }

    [HttpPut("{targetType}/{targetId}")]
    public async Task<IActionResult> Upsert(
        string targetType,
        string targetId,
        [FromBody] UserNoteRequest request,
        CancellationToken cancellationToken)
        => Map(await application.UpsertAsync(new UserNoteUpsertRequestMessage
        {
            OwnerSubject = Owner,
            TargetType = targetType,
            TargetId = targetId,
            Note = request.Note
        }, cancellationToken));

    [HttpDelete("{targetType}/{targetId}")]
    public async Task<IActionResult> Delete(
        string targetType,
        string targetId,
        CancellationToken cancellationToken)
    {
        var response = await application.DeleteAsync(new UserNoteDeleteRequestMessage
        {
            OwnerSubject = Owner,
            TargetType = targetType,
            TargetId = targetId
        }, cancellationToken);
        return response is { Success: true } or { ErrorCode: "not_found" }
            ? NoContent()
            : Map(response);
    }

    private string Owner => currentOwner.Subject
        ?? throw new InvalidOperationException("The fixed Lite owner is unavailable.");

    private IActionResult Map(UserNoteResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (response.Success)
            return Ok(response.Note);
        return Error(response.ErrorCode, response.ErrorMessage);
    }

    private IActionResult Map(UserNoteSearchResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        return response.Success ? Ok(response) : Error(response.ErrorCode, response.ErrorMessage);
    }

    private IActionResult Error(string? code, string? message) => code switch
    {
        "validation" => BadRequest(message),
        "not_found" => NotFound(message),
        "conflict" => Conflict(message),
        _ => StatusCode(StatusCodes.Status500InternalServerError, message)
    };
}

public sealed record UserNoteRequest(string Note);
