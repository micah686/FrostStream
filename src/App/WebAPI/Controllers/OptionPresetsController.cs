using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace WebAPI.Controllers;

/// <summary>
/// CRUD for stored yt-dlp option presets. Round-trips through NATS request/reply to
/// DataBridge's <c>OptionPresetCrudConsumerService</c>, which owns the
/// <c>download_option_presets</c> table.
/// </summary>
[ApiController]
[Route("api/option-presets")]
public class OptionPresetsController(IMessageBus messageBus, ILogger<OptionPresetsController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
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
    public async Task<ActionResult<OptionPresetResponse>> Get(string key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            OptionPresetSubjects.GetPreset,
            new OptionPresetGetRequestMessage { Key = key },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet]
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

public sealed class OptionPresetCreateRequest
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(2000)]
    public string? Description { get; init; }

    [Required]
    public required YtDlpOptions YtDlpOptions { get; init; }
}

public sealed class OptionPresetUpdateRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(2000)]
    public string? Description { get; init; }

    [Required]
    public required YtDlpOptions YtDlpOptions { get; init; }
}

public sealed class OptionPresetResponse
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required YtDlpOptions YtDlpOptions { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
