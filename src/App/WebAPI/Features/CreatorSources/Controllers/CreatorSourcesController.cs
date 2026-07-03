using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Database;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Common;
using WebAPI.Features.DownloadConfigSets;
using WebAPI.Features.CreatorSources.Models;

namespace WebAPI.Features.CreatorSources.Controllers;

[ApiController]
[Route("api/creator-sources")]
public sealed class CreatorSourcesController(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    IClock clock,
    ILogger<CreatorSourcesController> logger) : ControllerBase
{
    private const string ChannelAssetRefreshTaskType = "channel_asset_refresh";
    private const string ChannelMediaListTaskType = "channel_media_list";
    private const string ManualChannelDownloadScheduleKey = "manual-channel-download";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpPost]
    [Endpoint(EndpointIds.CreatorSourcesCreate)]
    [EndpointSummary("Create a creator discovery source")]
    [EndpointDescription("Registers a creator or channel source for recurring discovery scans. The platform, source type, URL, scan enablement, incremental paging thresholds, full-rescan interval, and metadata refresh window are validated and persisted by DataBridge.")]
    public async Task<ActionResult<CreatorSourceResponse>> Create(
        [FromBody] CreatorSourceCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!YtDlpSourceUrlValidator.TryValidate(request.SourceUrl, out var validationError))
            return BadRequest(validationError);

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
                MetadataRefreshWindow = request.MetadataRefreshWindow,
                ProviderQueryLimits = request.ProviderQueryLimits
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpPost("channel-downloads")]
    [Endpoint(EndpointIds.CreatorSourcesDownloadChannel)]
    [EndpointSummary("Queue a full channel download")]
    [EndpointDescription("Creates or reuses a creator channel source, then queues a targeted full channel scan. The scan uses the same creator-discovery pipeline as scheduled channel downloads, so discovered videos are tracked as channel media and unchanged known videos are not re-enqueued.")]
    public async Task<ActionResult<ChannelDownloadResponse>> DownloadChannel(
        [FromBody] ChannelDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (!YtDlpSourceUrlValidator.TryValidate(request.SourceUrl, out var validationError))
            return BadRequest(validationError);

        var sourceResponse = await SendAsync(
            CreatorDiscoverySubjects.CreateOrReuseSource,
            new CreatorSourceCreateOrReuseRequestMessage
            {
                Platform = request.Platform,
                SourceType = request.SourceType,
                SourceUrl = request.SourceUrl,
                ScanEnabled = true,
                ProviderQueryLimits = request.ProviderQueryLimits
            },
            cancellationToken);

        if (sourceResponse is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process creator source request.");
        if (!sourceResponse.Success)
            return MapErrorResponse(sourceResponse);
        if (sourceResponse.Entity is null)
            return StatusCode(StatusCodes.Status502BadGateway, "Creator source service returned an invalid response.");

        var now = clock.GetCurrentInstant();
        var idempotencyKey = $"manual-channel-download:{sourceResponse.Entity.Id}:{Guid.NewGuid():N}";
        var subject = AuthConstants.FindSubject(User);
        ResolvedDownloadConfigSet resolved;
        try
        {
            var (config, error) = await DownloadConfigSetResolver.ResolveAsync(
                messageBus,
                subject,
                request.ConfigSetKey,
                request.StorageKey,
                request.CookieProfileKey,
                request.YtDlpOptions,
                request.EncodeForPlaylist,
                request.AudioFormat,
                request.Priority,
                request.FetchComments,
                cancellationToken);
            if (error is not null)
                return BadRequest(error);
            resolved = config!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving channel download config set {ConfigSetKey}.", request.ConfigSetKey);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to resolve download config set.");
        }

        var message = new ChannelMediaListRequested
        {
            ScheduleKey = ManualChannelDownloadScheduleKey,
            TaskType = ChannelMediaListTaskType,
            DueWindowUtc = now,
            IdempotencyKey = idempotencyKey,
            OccurredAt = now,
            TargetSourceId = sourceResponse.Entity.Id,
            StorageKey = resolved.StorageKey,
            RequestedBy = subject,
            ConfigSetKey = resolved.ConfigSetKey,
            EncodeForPlaylist = resolved.EncodeForPlaylist,
            AudioFormat = resolved.AudioFormat,
            CookieSecretPath = resolved.CookieSecretPath,
            YtDlpOptions = resolved.YtDlpOptions,
            Priority = resolved.Priority,
            FetchComments = resolved.FetchComments,
            ProviderQueryLimits = request.ProviderQueryLimits
        };

        try
        {
            await publisher.PublishAsync(
                BackgroundJobSubjects.ChannelMediaListRequest,
                message,
                messageId: idempotencyKey,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed enqueueing manual channel download for source {SourceId}", sourceResponse.Entity.Id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to enqueue channel download.");
        }

        return Accepted(new ChannelDownloadResponse(
            sourceResponse.Entity.Id,
            sourceResponse.Entity.SourceUrl,
            sourceResponse.Entity.Platform,
            sourceResponse.Entity.SourceType,
            Queued: true,
            idempotencyKey));
    }

    [HttpPut("{id:long}")]
    [Endpoint(EndpointIds.CreatorSourcesUpdate)]
    [EndpointSummary("Update a creator discovery source")]
    [EndpointDescription("Replaces the discovery configuration for an existing creator source. The complete platform, source URL, scan controls, paging thresholds, rescan interval, and metadata refresh window are sent to DataBridge for validation and persistence.")]
    public async Task<ActionResult<CreatorSourceResponse>> Update(
        long id,
        [FromBody] CreatorSourceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!YtDlpSourceUrlValidator.TryValidate(request.SourceUrl, out var validationError))
            return BadRequest(validationError);

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
                MetadataRefreshWindow = request.MetadataRefreshWindow,
                ProviderQueryLimits = request.ProviderQueryLimits
            },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet("{id:long}")]
    [Endpoint(EndpointIds.CreatorSourcesGet)]
    [EndpointSummary("Get a creator discovery source")]
    [EndpointDescription("Retrieves a creator source by numeric identifier, including its scan configuration, last successful and full scan timestamps, high-water mark, and audit timestamps. Returns 404 when the source does not exist.")]
    public async Task<ActionResult<CreatorSourceResponse>> Get(long id, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            CreatorDiscoverySubjects.GetSource,
            new CreatorSourceGetRequestMessage { Id = id },
            cancellationToken);

        return MapEntityResponse(response);
    }

    [HttpGet]
    [Endpoint(EndpointIds.CreatorSourcesList)]
    [EndpointSummary("List creator discovery sources")]
    [EndpointDescription("Returns all configured creator discovery sources with their scanning policies and latest discovery state. The list is obtained from DataBridge through request/reply and returns 503 if the service cannot be reached.")]
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

    [HttpPost("{id:long}/refresh-assets")]
    [Endpoint(EndpointIds.CreatorSourcesRefreshAssets)]
    [EndpointSummary("Queue a creator asset refresh")]
    [EndpointDescription("Verifies that the creator source exists, then queues an asynchronous refresh of its avatar, banner, and related channel assets. The force query parameter controls whether cached assets may be replaced; a successful request returns 202 with the queued source identifier.")]
    public async Task<IActionResult> RefreshAssets(
        long id,
        [FromQuery] bool force,
        CancellationToken cancellationToken)
    {
        var getResponse = await SendAsync(
            CreatorDiscoverySubjects.GetSource,
            new CreatorSourceGetRequestMessage { Id = id },
            cancellationToken);

        if (getResponse is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process creator source request.");
        if (!getResponse.Success)
            return MapErrorResponse(getResponse);
        if (getResponse.Entity is null)
            return NotFound($"Creator source '{id}' was not found.");

        var now = clock.GetCurrentInstant();
        var idempotencyKey = $"manual:{id}:{Guid.NewGuid():N}";
        var message = new ChannelAssetRefreshRequested
        {
            ScheduleKey = "manual",
            TaskType = ChannelAssetRefreshTaskType,
            DueWindowUtc = now,
            IdempotencyKey = idempotencyKey,
            OccurredAt = now,
            TargetSourceId = id,
            Force = force
        };

        try
        {
            await publisher.PublishAsync(
                BackgroundJobSubjects.ChannelAssetRefreshRequest,
                message,
                messageId: idempotencyKey,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed enqueueing channel asset refresh for source {SourceId}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to enqueue channel asset refresh.");
        }

        return Accepted(new { queued = true, sourceId = id, force });
    }

    [HttpDelete("{id:long}")]
    [Endpoint(EndpointIds.CreatorSourcesDelete)]
    [EndpointSummary("Delete a creator discovery source")]
    [EndpointDescription("Deletes the creator discovery source identified by its numeric ID, preventing future scheduled discovery scans for that source. Successful deletion returns 204; missing sources return 404 and conflicting deletions return 409.")]
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

    [HttpGet("{id:long}/ignored-media")]
    [Endpoint(EndpointIds.CreatorSourcesListIgnoredMedia)]
    [EndpointSummary("List ignored videos for a creator source")]
    [EndpointDescription("Returns videos that were suppressed by a config-set ignore keyword during a user-initiated full channel download for this source, including the keyword that matched. Background monitoring never ignores videos.")]
    public async Task<ActionResult<IReadOnlyCollection<IgnoredMediaResponse>>> ListIgnoredMedia(
        long id,
        CancellationToken cancellationToken)
    {
        ListIgnoredMediaResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<ListIgnoredMediaRequestMessage, ListIgnoredMediaResponseMessage>(
                CreatorDiscoverySubjects.ListIgnoredMedia,
                new ListIgnoredMediaRequestMessage { CreatorSourceId = id },
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing ignored media for source {SourceId}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to list ignored media.");
        }

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to list ignored media.");
        if (!response.Success)
            return StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Failed to list ignored media.");

        return Ok((response.Items ?? Array.Empty<IgnoredMediaDto>()).Select(x => new IgnoredMediaResponse
        {
            Id = x.Id,
            CreatorSourceId = x.CreatorSourceId,
            Title = x.Title,
            CanonicalUrl = x.CanonicalUrl,
            IgnoredKeyword = x.IgnoredKeyword,
            FirstSeenAt = x.FirstSeenAt,
            LastSeenAt = x.LastSeenAt
        }).ToArray());
    }

    [Obsolete("remove this endpoint later")]
    [HttpPost("discovered-media/{id:long}/force-queue")]
    [Endpoint(EndpointIds.CreatorSourcesForceQueueMedia)]
    [EndpointSummary("Force-queue an ignored video")]
    [EndpointDescription("Clears the ignored state of a discovered video and queues it for download with force enabled, bypassing the ignore keywords. The download configuration is resolved from the supplied config set or overrides.")]
    public async Task<ActionResult<ForceQueueResponse>> ForceQueueMedia(
        long id,
        [FromBody] ForceQueueMediaRequest request,
        CancellationToken cancellationToken)
    {
        var subject = AuthConstants.FindSubject(User);
        if (string.IsNullOrWhiteSpace(subject))
            return Unauthorized();

        ResolvedDownloadConfigSet resolved;
        try
        {
            var (config, error) = await DownloadConfigSetResolver.ResolveAsync(
                messageBus,
                subject,
                request.ConfigSetKey,
                request.StorageKey,
                request.CookieProfileKey,
                request.YtDlpOptions,
                request.EncodeForPlaylist,
                request.AudioFormat,
                request.Priority,
                request.FetchComments,
                cancellationToken);
            if (error is not null)
                return BadRequest(error);
            resolved = config!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving force-queue config set {ConfigSetKey}.", request.ConfigSetKey);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to resolve download config set.");
        }

        ForceQueueOperationResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<ForceQueueDiscoveredMediaRequestMessage, ForceQueueOperationResponseMessage>(
                CreatorDiscoverySubjects.ForceQueueDiscoveredMedia,
                new ForceQueueDiscoveredMediaRequestMessage
                {
                    DiscoveredMediaId = id,
                    RequestedBy = subject,
                    StorageKey = resolved.StorageKey,
                    CookieSecretPath = resolved.CookieSecretPath,
                    YtDlpOptions = resolved.YtDlpOptions,
                    EncodeForPlaylist = resolved.EncodeForPlaylist,
                    AudioFormat = resolved.AudioFormat,
                    Priority = resolved.Priority,
                    FetchComments = resolved.FetchComments
                },
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed force-queueing discovered media {MediaId}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to force-queue media.");
        }

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to force-queue media.");
        if (!response.Success)
        {
            return response.ErrorCode switch
            {
                "not_found" => NotFound(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Failed to force-queue media.")
            };
        }

        return Accepted(new ForceQueueResponse(id, response.JobId ?? Guid.Empty, Queued: true));
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
            ProviderQueryLimits = dto.ProviderQueryLimits,
            LastSuccessfulScanAt = dto.LastSuccessfulScanAt,
            LastFullScanAt = dto.LastFullScanAt,
            LastSeenHighWatermark = dto.LastSeenHighWatermark,
            NextFullScanStartIndex = dto.NextFullScanStartIndex,
            CreatedAt = dto.CreatedAt,
            LastUpdated = dto.LastUpdated
        };
}
