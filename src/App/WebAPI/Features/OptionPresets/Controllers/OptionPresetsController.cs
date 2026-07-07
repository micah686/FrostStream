using System.Text.Json;
using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.OptionPresets.Models;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.OptionPresets.Controllers;

/// <summary>
/// CRUD for stored yt-dlp option presets. Round-trips through NATS request/reply to
/// DataBridge's <c>OptionPresetCrudConsumerService</c>, which owns the
/// <c>download_option_presets</c> table.
/// </summary>
[ApiController]
[Route("api/user/option-presets")]
public class OptionPresetsController(IMessageBus messageBus, ILogger<OptionPresetsController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
    [Endpoint(EndpointIds.OptionPresetsCreate)]
    [EndpointSummary("Create a yt-dlp option preset")]
    [EndpointDescription("Stores a named set of yt-dlp options that can be referenced by preset-based download requests. The options are serialized and sent to DataBridge for validation and persistence; duplicate keys return 409 and invalid preset data returns 400.")]
    public async Task<ActionResult<OptionPresetResponse>> Create(
        [FromBody] OptionPresetCreateRequest request,
        CancellationToken cancellationToken)
    {
        var json = SerializeOptions(request.YtDlpOptions);

        var response = await SendAsync(
            OptionPresetSubjects.CreatePreset,
            new OptionPresetCreateRequestMessage
            {
                Key = request.Key,
                Name = request.Name,
                Description = request.Description,
                YtDlpOptionsJson = json
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpPut("{key}")]
    [Endpoint(EndpointIds.OptionPresetsUpdate)]
    [EndpointSummary("Update a yt-dlp option preset")]
    [EndpointDescription("Replaces the display metadata and yt-dlp option payload for an existing preset identified by its key. DataBridge validates and persists the complete replacement; unknown keys return 404 and invalid updates return 400.")]
    public async Task<ActionResult<OptionPresetResponse>> Update(
        string key,
        [FromBody] OptionPresetUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var json = SerializeOptions(request.YtDlpOptions);

        var response = await SendAsync(
            OptionPresetSubjects.UpdatePreset,
            new OptionPresetUpdateRequestMessage
            {
                Key = key,
                Name = request.Name,
                Description = request.Description,
                YtDlpOptionsJson = json
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet("{key}")]
    [Endpoint(EndpointIds.OptionPresetsGet)]
    [EndpointSummary("Get a yt-dlp option preset")]
    [EndpointDescription("Retrieves one stored option preset by key and deserializes its persisted yt-dlp options into the API response model. Returns 404 when the key does not exist and 503 when the DataBridge request cannot be completed.")]
    public async Task<ActionResult<OptionPresetResponse>> Get(string key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            OptionPresetSubjects.GetPreset,
            new OptionPresetGetRequestMessage { Key = key },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet]
    [Endpoint(EndpointIds.OptionPresetsList)]
    [EndpointSummary("List yt-dlp option presets")]
    [EndpointDescription("Returns all stored yt-dlp option presets, including their names, descriptions, options, and timestamps. Each persisted options document is deserialized for the response; an unreadable document is represented with an empty options object instead of failing the entire list.")]
    public async Task<ActionResult<IReadOnlyCollection<OptionPresetResponse>>> List(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            OptionPresetSubjects.ListPresets,
            new OptionPresetListRequestMessage(),
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process preset list request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return Ok((response.Items ?? Array.Empty<OptionPresetDto>()).Select(MapDto).ToArray());
    }

    [HttpDelete("{key}")]
    [Endpoint(EndpointIds.OptionPresetsDelete)]
    [EndpointSummary("Delete a yt-dlp option preset")]
    [EndpointDescription("Deletes the stored option preset identified by its key. A successful deletion returns 204; unknown keys return 404 and presets that cannot be deleted because of a conflict return 409.")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            OptionPresetSubjects.DeletePreset,
            new OptionPresetDeleteRequestMessage { Key = key },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process preset delete request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return NoContent();
    }

    private async Task<OptionPresetOperationResponseMessage?> SendAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, OptionPresetOperationResponseMessage>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing option-preset request on subject '{Subject}'", subject);
            return null;
        }
    }

    private ActionResult<OptionPresetResponse> MapEntityResponse(OptionPresetOperationResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process preset request.");
        if (!response.Success)
            return MapErrorResponse(response);
        if (response.Entity is null)
            return StatusCode(StatusCodes.Status502BadGateway, "Preset service returned an invalid response.");

        return Ok(MapDto(response.Entity));
    }

    private ActionResult MapErrorResponse(OptionPresetOperationResponseMessage response)
        => response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "conflict" => Conflict(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Preset request failed.")
        };

    private static string SerializeOptions(YtDlpOptions options)
        => JsonSerializer.Serialize(options);

    private static OptionPresetResponse MapDto(OptionPresetDto dto)
    {
        YtDlpOptions options;
        try
        {
            options = JsonSerializer.Deserialize<YtDlpOptions>(dto.YtDlpOptionsJson) ?? new YtDlpOptions();
        }
        catch (JsonException)
        {
            options = new YtDlpOptions();
        }

        return new OptionPresetResponse
        {
            Id = dto.Id,
            Key = dto.Key,
            Name = dto.Name,
            Description = dto.Description,
            YtDlpOptions = options,
            CreatedAt = dto.CreatedAt,
            LastUpdated = dto.LastUpdated
        };
    }
}
