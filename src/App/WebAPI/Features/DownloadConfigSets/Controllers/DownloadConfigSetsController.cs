using System.Text.Json;
using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.DownloadConfigSets.Models;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.DownloadConfigSets.Controllers;

[ApiController]
[Route("api/download-config-sets")]
public sealed class DownloadConfigSetsController(
    IMessageBus messageBus,
    ILogger<DownloadConfigSetsController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
    [Endpoint(EndpointIds.DownloadConfigSetsCreate)]
    [EndpointSummary("Create a download config set")]
    [EndpointDescription("Creates a reusable download configuration set owned by the authenticated user. The config can include storage target, cookie profile key, yt-dlp options, playlist audio encoding options, priority, and comment-fetch behavior.")]
    public async Task<ActionResult<DownloadConfigSetResponse>> Create(
        [FromBody] DownloadConfigSetCreateRequest request,
        CancellationToken cancellationToken)
    {
        var owner = RequireSubject();
        if (owner is null)
            return Unauthorized();

        var response = await SendAsync(DownloadConfigSetSubjects.Create, ToMessage(owner, request), cancellationToken);
        return MapEntityResponse(response);
    }

    [HttpPut("{key}")]
    [Endpoint(EndpointIds.DownloadConfigSetsUpdate)]
    [EndpointSummary("Update a download config set")]
    [EndpointDescription("Replaces a reusable download configuration set owned by the authenticated user. The key comes from the route and remains scoped to the caller, so identical keys can exist for different users.")]
    public async Task<ActionResult<DownloadConfigSetResponse>> Update(
        string key,
        [FromBody] DownloadConfigSetUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var owner = RequireSubject();
        if (owner is null)
            return Unauthorized();

        var response = await SendAsync(DownloadConfigSetSubjects.Update, ToUpdateMessage(owner, key, request), cancellationToken);
        return MapEntityResponse(response);
    }

    [HttpGet("{key}")]
    [Endpoint(EndpointIds.DownloadConfigSetsGet)]
    [EndpointSummary("Get a download config set")]
    [EndpointDescription("Retrieves one reusable download configuration set by key for the authenticated user. Other users' config sets are never visible even when they use the same key.")]
    public async Task<ActionResult<DownloadConfigSetResponse>> Get(string key, CancellationToken cancellationToken)
    {
        var owner = RequireSubject();
        if (owner is null)
            return Unauthorized();

        var response = await SendAsync(
            DownloadConfigSetSubjects.Get,
            new DownloadConfigSetGetRequestMessage { OwnerSubject = owner, Key = key },
            cancellationToken);
        return MapEntityResponse(response);
    }

    [HttpGet]
    [Endpoint(EndpointIds.DownloadConfigSetsList)]
    [EndpointSummary("List download config sets")]
    [EndpointDescription("Lists all reusable download configuration sets owned by the authenticated user, ordered by key. The response includes each config's storage, cookie, yt-dlp, audio encoding, priority, and comment-fetch options.")]
    public async Task<ActionResult<IReadOnlyCollection<DownloadConfigSetResponse>>> List(CancellationToken cancellationToken)
    {
        var owner = RequireSubject();
        if (owner is null)
            return Unauthorized();

        var response = await SendAsync(
            DownloadConfigSetSubjects.List,
            new DownloadConfigSetListRequestMessage { OwnerSubject = owner },
            cancellationToken);
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process download config set list request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return Ok((response.Items ?? Array.Empty<DownloadConfigSetDto>()).Select(MapDto).ToArray());
    }

    [HttpDelete("{key}")]
    [Endpoint(EndpointIds.DownloadConfigSetsDelete)]
    [EndpointSummary("Delete a download config set")]
    [EndpointDescription("Deletes one reusable download configuration set owned by the authenticated user. Deleting a config set does not affect jobs that were already queued using the config's resolved values.")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        var owner = RequireSubject();
        if (owner is null)
            return Unauthorized();

        var response = await SendAsync(
            DownloadConfigSetSubjects.Delete,
            new DownloadConfigSetDeleteRequestMessage { OwnerSubject = owner, Key = key },
            cancellationToken);
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process download config set delete request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return NoContent();
    }

    private async Task<DownloadConfigSetOperationResponseMessage?> SendAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, DownloadConfigSetOperationResponseMessage>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing download config set request on subject {Subject}.", subject);
            return null;
        }
    }

    private string? RequireSubject()
    {
        var subject = AuthConstants.FindSubject(User);
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    private static DownloadConfigSetCreateRequestMessage ToMessage(string owner, DownloadConfigSetCreateRequest request)
        => new()
        {
            OwnerSubject = owner,
            Key = request.Key,
            Name = request.Name,
            Description = request.Description,
            StorageKey = request.StorageKey,
            CookieProfileKey = request.CookieProfileKey,
            YtDlpOptionsJson = request.YtDlpOptions is null ? null : JsonSerializer.Serialize(request.YtDlpOptions),
            IgnoreKeywords = request.IgnoreKeywords,
            EncodeForPlaylist = request.EncodeForPlaylist,
            AudioFormat = request.AudioFormat,
            Priority = request.Priority,
            FetchComments = request.FetchComments
        };

    private static DownloadConfigSetUpdateRequestMessage ToUpdateMessage(string owner, string key, DownloadConfigSetUpdateRequest request)
        => new()
        {
            OwnerSubject = owner,
            Key = key,
            Name = request.Name,
            Description = request.Description,
            StorageKey = request.StorageKey,
            CookieProfileKey = request.CookieProfileKey,
            YtDlpOptionsJson = request.YtDlpOptions is null ? null : JsonSerializer.Serialize(request.YtDlpOptions),
            IgnoreKeywords = request.IgnoreKeywords,
            EncodeForPlaylist = request.EncodeForPlaylist,
            AudioFormat = request.AudioFormat,
            Priority = request.Priority,
            FetchComments = request.FetchComments
        };

    private ActionResult<DownloadConfigSetResponse> MapEntityResponse(DownloadConfigSetOperationResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process download config set request.");
        if (!response.Success)
            return MapErrorResponse(response);
        if (response.Entity is null)
            return StatusCode(StatusCodes.Status502BadGateway, "Download config set service returned an invalid response.");

        return Ok(MapDto(response.Entity));
    }

    private ActionResult MapErrorResponse(DownloadConfigSetOperationResponseMessage response)
        => response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "conflict" => Conflict(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Download config set request failed.")
        };

    private static DownloadConfigSetResponse MapDto(DownloadConfigSetDto dto)
        => new()
        {
            Id = dto.Id,
            Key = dto.Key,
            Name = dto.Name,
            Description = dto.Description,
            StorageKey = dto.StorageKey,
            CookieProfileKey = dto.CookieProfileKey,
            YtDlpOptions = ParseOptionsJson(dto.YtDlpOptionsJson),
            IgnoreKeywords = dto.IgnoreKeywords,
            EncodeForPlaylist = dto.EncodeForPlaylist,
            AudioFormat = dto.AudioFormat,
            Priority = dto.Priority,
            FetchComments = dto.FetchComments
        };

    private static JsonElement? ParseOptionsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
