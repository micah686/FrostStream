using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/creator-sources")]
public sealed class CreatorSourcesController(IMessageBus messageBus, ILogger<CreatorSourcesController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
    public async Task<ActionResult<CreatorSourceResponse>> Create(
        [FromBody] CreatorSourceCreateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            CreatorDiscoverySubjects.CreateSource,
            new CreatorSourceCreateRequestMessage
            {
                Platform = request.Platform,
                SourceType = request.SourceType,
                SourceUrl = request.SourceUrl,
                ScanEnabled = request.ScanEnabled,
                IncrementalPageSize = request.IncrementalPageSize,
                ConsecutiveKnownThreshold = request.ConsecutiveKnownThreshold,
                FullRescanIntervalDays = request.FullRescanIntervalDays,
                MetadataRefreshWindow = request.MetadataRefreshWindow
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpPut("{id:long}")]
    public async Task<ActionResult<CreatorSourceResponse>> Update(
        long id,
        [FromBody] CreatorSourceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            CreatorDiscoverySubjects.UpdateSource,
            new CreatorSourceUpdateRequestMessage
            {
                Id = id,
                Platform = request.Platform,
                SourceType = request.SourceType,
                SourceUrl = request.SourceUrl,
                ScanEnabled = request.ScanEnabled,
                IncrementalPageSize = request.IncrementalPageSize,
                ConsecutiveKnownThreshold = request.ConsecutiveKnownThreshold,
                FullRescanIntervalDays = request.FullRescanIntervalDays,
                MetadataRefreshWindow = request.MetadataRefreshWindow
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<CreatorSourceResponse>> Get(long id, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            CreatorDiscoverySubjects.GetSource,
            new CreatorSourceGetRequestMessage { Id = id },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<CreatorSourceResponse>>> List(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            CreatorDiscoverySubjects.ListSources,
            new CreatorSourceListRequestMessage(),
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process creator source list request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return Ok((response.Items ?? Array.Empty<CreatorSourceDto>()).Select(MapDto).ToArray());
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            CreatorDiscoverySubjects.DeleteSource,
            new CreatorSourceDeleteRequestMessage { Id = id },
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process creator source delete request.");
        if (!response.Success)
            return MapErrorResponse(response);

        return NoContent();
    }

    private async Task<CreatorSourceOperationResponseMessage?> SendAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, CreatorSourceOperationResponseMessage>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing creator source request on subject '{Subject}'", subject);
            return null;
        }
    }

    private ActionResult<CreatorSourceResponse> MapEntityResponse(CreatorSourceOperationResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process creator source request.");
        if (!response.Success)
            return MapErrorResponse(response);
        if (response.Entity is null)
            return StatusCode(StatusCodes.Status502BadGateway, "Creator source service returned an invalid response.");

        return Ok(MapDto(response.Entity));
    }

    private ActionResult MapErrorResponse(CreatorSourceOperationResponseMessage response)
        => response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "conflict" => Conflict(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Creator source request failed.")
        };

    private static CreatorSourceResponse MapDto(CreatorSourceDto dto)
        => new()
        {
            Id = dto.Id,
            Platform = dto.Platform,
            SourceType = dto.SourceType,
            SourceUrl = dto.SourceUrl,
            ScanEnabled = dto.ScanEnabled,
            IncrementalPageSize = dto.IncrementalPageSize,
            ConsecutiveKnownThreshold = dto.ConsecutiveKnownThreshold,
            FullRescanIntervalDays = dto.FullRescanIntervalDays,
            MetadataRefreshWindow = dto.MetadataRefreshWindow,
            LastSuccessfulScanAt = dto.LastSuccessfulScanAt,
            LastFullScanAt = dto.LastFullScanAt,
            LastSeenHighWatermark = dto.LastSeenHighWatermark,
            CreatedAt = dto.CreatedAt,
            LastUpdated = dto.LastUpdated
        };
}

public abstract class CreatorSourceRequestBase
{
    [Required]
    [StringLength(50, MinimumLength = 1)]
    public required string Platform { get; init; }

    public CreatorSourceType SourceType { get; init; }

    [Required]
    [Url]
    [StringLength(4096, MinimumLength = 1)]
    public required string SourceUrl { get; init; }

    public bool ScanEnabled { get; init; } = true;

    [Range(1, 500)]
    public int IncrementalPageSize { get; init; } = 50;

    [Range(1, 500)]
    public int ConsecutiveKnownThreshold { get; init; } = 25;

    [Range(1, 365)]
    public int FullRescanIntervalDays { get; init; } = 30;

    [Range(1, 500)]
    public int MetadataRefreshWindow { get; init; } = 25;
}

public sealed class CreatorSourceCreateRequest : CreatorSourceRequestBase;

public sealed class CreatorSourceUpdateRequest : CreatorSourceRequestBase;

public sealed class CreatorSourceResponse
{
    public required long Id { get; init; }
    public required string Platform { get; init; }
    public required CreatorSourceType SourceType { get; init; }
    public required string SourceUrl { get; init; }
    public required bool ScanEnabled { get; init; }
    public required int IncrementalPageSize { get; init; }
    public required int ConsecutiveKnownThreshold { get; init; }
    public required int FullRescanIntervalDays { get; init; }
    public required int MetadataRefreshWindow { get; init; }
    public Instant? LastSuccessfulScanAt { get; init; }
    public Instant? LastFullScanAt { get; init; }
    public string? LastSeenHighWatermark { get; init; }
    public required Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
