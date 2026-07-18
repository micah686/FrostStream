using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conduit.NATS;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Auth;
using Shared.Messaging;
using Shared.Storage;
using WebAPI.Auth;
using WebAPI.Features.Media.Casting;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// Server-side casting surface: the WebAPI discovers cast receivers on its own network via each
/// enabled protocol and drives them directly (connect, load, transport control), so any browser can cast
/// without vendor browser SDKs. Media reaches the device through this server's stream endpoints,
/// authenticated by a cast token minted here: a faststart H.264/AAC MP4 original is served
/// progressively (range requests) from <see cref="MediaWatchController"/>, anything else goes
/// through the HLS stream rendition served by <see cref="MediaStreamController"/>.
/// </summary>
[ApiController]
[Route("api/cast")]
public sealed class CastController(
    CastSessionManager sessions,
    ICastDeviceRegistry devices,
    CastTokenService castTokens,
    MediaAccessChecker accessChecker,
    AudioRenditionResolver audioRenditions,
    StreamRenditionResolver streamRenditions,
    IBlobStorageProvider blobStorageProvider,
    CastMediaUrlBuilder urlBuilder,
    IMessageBus messageBus,
    ILogger<CastController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        // Match the MVC JSON contract (camelCase, string enums, ISO NodaTime) so SSE payloads are
        // shaped exactly like the REST DTOs the client also consumes.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    // ── Devices ──────────────────────────────────────────────────────────────────────

    [HttpGet("devices")]
    [Endpoint(EndpointIds.CastDevicesList)]
    [EndpointSummary("List cast devices on the server's network")]
    [EndpointDescription("Discovers cast receivers reachable from the server via enabled protocols and returns their stable ids, protocol names, display names, and addresses. Results are cached briefly; pass refresh=true to force a new network scan, which takes a few seconds. An empty list means no device answered on the server's network segment.")]
    public async Task<ActionResult<IReadOnlyList<CastDeviceDto>>> ListDevices(
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var discovered = await devices.GetDevicesAsync(refresh, cancellationToken);
        return Ok(discovered);
    }

    // ── Session lifecycle ────────────────────────────────────────────────────────────

    [HttpPost("devices/{deviceId}/session")]
    [Endpoint(EndpointIds.CastSessionsStart)]
    [EndpointSummary("Start casting a media item to a device")]
    [EndpointDescription("Connects to the given cast device and loads the requested media item from this server's stream endpoints, authenticated with a freshly minted single-media cast token. Compatible MP4 originals stream progressively; everything else is served as the H.264/AAC HLS rendition, and audioOnly=true serves the opus audio rendition. Returns 202 while a required rendition is still being prepared. Supports an optional WebVTT subtitle track by language and an optional start position. Replaces whatever the device is currently playing when a session already exists.")]
    public async Task<IActionResult> StartSession(
        string deviceId,
        [FromBody] StartCastSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.StartPositionSeconds is < 0)
        {
            return BadRequest("startPositionSeconds must not be negative.");
        }

        var captionType = string.IsNullOrWhiteSpace(request.CaptionType) ? null : request.CaptionType.Trim();
        if (captionType is not (null or "subtitles" or "automatic_captions"))
        {
            return BadRequest("captionType must be 'subtitles' or 'automatic_captions'.");
        }

        if (await accessChecker.CheckWatchAccessAsync(User, request.MediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        var (baseUrl, baseUrlError) = urlBuilder.ResolveBaseUrl(Request);
        if (baseUrl is null)
        {
            return BadRequest(baseUrlError);
        }

        // Resolve what the device will actually fetch — fail here instead of on the TV screen.
        string contentType;
        var useHlsRendition = false;
        if (request.AudioOnly)
        {
            var (error, rendition) = await audioRenditions.ResolveAsync(
                request.MediaGuid,
                storageKey: null,
                sourceVersion: null,
                createIfMissing: true,
                cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (rendition!.Status != AudioRenditionStatus.Ready || string.IsNullOrWhiteSpace(rendition.StoragePath))
            {
                // Same body shape as the watch endpoint's 202 so clients share retry handling.
                return Accepted(new
                {
                    rendition.RenditionId,
                    rendition.MediaGuid,
                    rendition.SourceVersion,
                    Status = rendition.Status.ToString().ToLowerInvariant()
                });
            }

            contentType = AudioRenditionHelpers.ContentType;
        }
        else
        {
            var (error, location) = await ResolveStreamLocationAsync(request.MediaGuid, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            // Faststart H.264/AAC MP4 originals stream directly with range support; everything
            // else is served as the H.264/AAC HLS rendition, encoded on first request.
            if (await CastStreamCompatibility.IsDirectStreamableAsync(
                    blobStorageProvider, location!.StorageKey, location.StoragePath, logger, cancellationToken))
            {
                contentType = VideoContentTypeOf(location.StoragePath);
            }
            else
            {
                var (renditionError, rendition) = await streamRenditions.ResolveAsync(
                    request.MediaGuid,
                    storageKey: null,
                    sourceVersion: null,
                    createIfMissing: true,
                    cancellationToken);
                if (renditionError is not null)
                {
                    return renditionError;
                }

                if (rendition!.Status != StreamRenditionStatus.Ready || string.IsNullOrWhiteSpace(rendition.StoragePath))
                {
                    // Same body shape as the watch endpoint's 202 so clients share retry handling.
                    return Accepted(new
                    {
                        rendition.RenditionId,
                        rendition.MediaGuid,
                        rendition.SourceVersion,
                        Status = rendition.Status.ToString().ToLowerInvariant()
                    });
                }

                useHlsRendition = true;
                contentType = StreamRenditionHelpers.HlsContentType;
            }
        }

        var metadata = await FetchMetadataAsync(request.MediaGuid, cancellationToken);
        var (token, tokenExpiresAt) = castTokens.Issue(User, request.MediaGuid);

        var contentUrl = useHlsRendition
            ? CastMediaUrlBuilder.BuildHlsManifestUrl(baseUrl, request.MediaGuid, token)
            : CastMediaUrlBuilder.BuildStreamUrl(baseUrl, request.MediaGuid, token, request.AudioOnly);
        var title = metadata?.Title ?? request.MediaGuid.ToString("D");
        var thumbnailUrl = metadata?.ThumbnailStoragePath is null
            ? null
            : CastMediaUrlBuilder.BuildThumbnailUrl(baseUrl, request.MediaGuid, token);

        CastSubtitleTrack? subtitle = null;
        if (!request.AudioOnly && !string.IsNullOrWhiteSpace(request.SubtitleLanguage))
        {
            var language = request.SubtitleLanguage.Trim();
            subtitle = new CastSubtitleTrack
            {
                Language = language,
                Url = CastMediaUrlBuilder.BuildCaptionUrl(
                    baseUrl, request.MediaGuid, language, captionType, token)
            };
        }

        var spec = new CastLoadSpec
        {
            MediaGuid = request.MediaGuid,
            Title = title,
            ContentUrl = contentUrl,
            ContentType = contentType,
            DurationSeconds = metadata?.DurationSeconds,
            ThumbnailUrl = thumbnailUrl,
            Subtitle = subtitle,
            StartPositionSeconds = request.StartPositionSeconds,
            TokenExpiresAt = tokenExpiresAt
        };

        logger.LogInformation(
            "Starting cast session for {DeviceId} using {ContentType} media URL {ContentUrl} (audioOnly: {AudioOnly}).",
            deviceId,
            spec.ContentType,
            spec.ContentUrl,
            request.AudioOnly);

        try
        {
            var session = await sessions.StartAsync(deviceId, spec, cancellationToken);
            return CreatedAtAction(nameof(GetSession), new { deviceId }, session);
        }
        catch (Exception ex) when (MapCastError(ex) is { } result)
        {
            return result;
        }
    }

    [HttpGet("sessions")]
    [Endpoint(EndpointIds.CastSessionsList)]
    [EndpointSummary("List active server-side cast sessions")]
    [EndpointDescription("Returns every cast session this server currently holds a live receiver connection for, including the target device, the media being cast, and the last-known playback snapshot. Sessions disappear from this list when the device disconnects or the session is explicitly ended.")]
    public ActionResult<IReadOnlyList<CastSessionDto>> ListSessions()
        => Ok(sessions.ListSessions());

    [HttpGet("sessions/{deviceId}")]
    [Endpoint(EndpointIds.CastSessionsGet)]
    [EndpointSummary("Get the active cast session for a device")]
    [EndpointDescription("Returns the cast session currently bound to the given device id, including the media being cast and the last-known playback snapshot (player state, position, duration, volume). Returns 404 when the device has no active server-side cast session.")]
    public ActionResult<CastSessionDto> GetSession(string deviceId)
        => sessions.GetSession(deviceId) is { } session
            ? Ok(session)
            : NotFound($"No active cast session for device '{deviceId}'.");

    [HttpDelete("sessions/{deviceId}")]
    [Endpoint(EndpointIds.CastSessionsDisconnect)]
    [EndpointSummary("End a cast session and disconnect the device")]
    [EndpointDescription("Tears down the server-side cast session for the given device: playback control is released, the receiver connection is closed, and any open status event streams for the session are completed with a final 'ended' event. The device itself returns to its idle screen shortly after.")]
    public async Task<IActionResult> Disconnect(string deviceId)
    {
        try
        {
            await sessions.DisconnectAsync(deviceId);
            return NoContent();
        }
        catch (Exception ex) when (MapCastError(ex) is { } result)
        {
            return result;
        }
    }

    // ── Transport controls ───────────────────────────────────────────────────────────

    [HttpPost("sessions/{deviceId}/play")]
    [Endpoint(EndpointIds.CastSessionsPlay)]
    [EndpointSummary("Resume playback on a cast session")]
    [EndpointDescription("Sends a play command to the receiver for the given device's active cast session and returns the updated session snapshot. Returns 404 when the device has no active session and 502 when the device stops answering commands.")]
    public Task<IActionResult> Play(string deviceId, CancellationToken cancellationToken)
        => ExecuteTransportAsync(() => sessions.PlayAsync(deviceId, cancellationToken));

    [HttpPost("sessions/{deviceId}/pause")]
    [Endpoint(EndpointIds.CastSessionsPause)]
    [EndpointSummary("Pause playback on a cast session")]
    [EndpointDescription("Sends a pause command to the receiver for the given device's active cast session and returns the updated session snapshot. Returns 404 when the device has no active session and 502 when the device stops answering commands.")]
    public Task<IActionResult> Pause(string deviceId, CancellationToken cancellationToken)
        => ExecuteTransportAsync(() => sessions.PauseAsync(deviceId, cancellationToken));

    [HttpPost("sessions/{deviceId}/stop")]
    [Endpoint(EndpointIds.CastSessionsStop)]
    [EndpointSummary("Stop playback on a cast session")]
    [EndpointDescription("Stops the media currently playing on the given device's active cast session while keeping the session and receiver connection alive, so another item can be cast without reconnecting. Returns 404 when the device has no active session and 502 when the device stops answering commands.")]
    public Task<IActionResult> Stop(string deviceId, CancellationToken cancellationToken)
        => ExecuteTransportAsync(() => sessions.StopAsync(deviceId, cancellationToken));

    [HttpPost("sessions/{deviceId}/seek")]
    [Endpoint(EndpointIds.CastSessionsSeek)]
    [EndpointSummary("Seek within a cast session's media")]
    [EndpointDescription("Seeks the receiver to an absolute position in seconds within the media currently playing on the given device's active cast session and returns the updated session snapshot. Returns 404 when the device has no active session and 502 when the device stops answering commands.")]
    public Task<IActionResult> Seek(
        string deviceId,
        [FromBody] CastSeekRequest request,
        CancellationToken cancellationToken)
        => request.Seconds < 0
            ? Task.FromResult<IActionResult>(BadRequest("seconds must not be negative."))
            : ExecuteTransportAsync(() => sessions.SeekAsync(deviceId, request.Seconds, cancellationToken));

    [HttpPost("sessions/{deviceId}/volume")]
    [Endpoint(EndpointIds.CastSessionsVolume)]
    [EndpointSummary("Set device volume or mute on a cast session")]
    [EndpointDescription("Sets the cast device's volume level (0.0 to 1.0) and/or mute state for the given device's active cast session; omitted fields are left unchanged. Returns the updated session snapshot, 404 when the device has no active session, and 502 when the device stops answering commands.")]
    public Task<IActionResult> SetVolume(
        string deviceId,
        [FromBody] CastVolumeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Level is null && request.Muted is null)
        {
            return Task.FromResult<IActionResult>(BadRequest("Provide 'level' and/or 'muted'."));
        }

        if (request.Level is < 0 or > 1)
        {
            return Task.FromResult<IActionResult>(BadRequest("level must be between 0.0 and 1.0."));
        }

        return ExecuteTransportAsync(() => sessions.SetVolumeAsync(deviceId, request.Level, request.Muted, cancellationToken));
    }

    // ── Live status (SSE) ────────────────────────────────────────────────────────────

    [HttpGet("sessions/{deviceId}/events")]
    [Endpoint(EndpointIds.CastSessionsEvents)]
    [EndpointSummary("Stream a cast session's status via SSE")]
    [EndpointDescription("Opens a Server-Sent Events stream of playback status for the given device's active cast session. The current snapshot is delivered immediately, 'status' events follow on every receiver transition or transport command, and a final 'ended' event is sent when the session ends or the device disconnects, after which the stream closes. Returns 404 when the device has no active session.")]
    public async Task StreamEvents(string deviceId, CancellationToken cancellationToken)
    {
        var subscription = sessions.Subscribe(deviceId);
        if (subscription is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (id, reader) = subscription.Value;
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        var feature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        feature?.DisableBuffering();

        // Serialize all writes to the response body — the heartbeat timer and the event loop both write.
        var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = SendHeartbeatsAsync(writeLock, heartbeatCts.Token);

        try
        {
            await foreach (var evt in reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(evt.Session, JsonOptions);
                await WriteFrameAsync(writeLock, $"event: {evt.Name}\ndata: {json}\n\n", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch { /* best-effort cleanup */ }
            sessions.Unsubscribe(deviceId, id);
            writeLock.Dispose();
        }
    }

    private async Task SendHeartbeatsAsync(SemaphoreSlim writeLock, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                // SSE comment line — keeps intermediaries from idling the connection out.
                await WriteFrameAsync(writeLock, ": keepalive\n\n", ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Client went away mid-write; the main loop's cancellation handles teardown.
                return;
            }
        }
    }

    private async Task WriteFrameAsync(SemaphoreSlim writeLock, string frame, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await Response.WriteAsync(frame, ct);
            await Response.Body.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> ExecuteTransportAsync(Func<Task<CastSessionDto>> command)
    {
        try
        {
            return Ok(await command());
        }
        catch (Exception ex) when (MapCastError(ex) is { } result)
        {
            return result;
        }
    }

    private IActionResult? MapCastError(Exception exception)
    {
        switch (exception)
        {
            case CastDeviceNotFoundException:
            case CastSessionNotFoundException:
                return NotFound(exception.Message);
            case CastDeviceUnreachableException:
                logger.LogWarning(exception, "Cast device command failed.");
                return StatusCode(StatusCodes.Status502BadGateway, exception.Message);
            default:
                return null;
        }
    }

    /// <summary>Confirms the media file exists and returns where the original lives in storage.</summary>
    private async Task<(IActionResult? Error, MediaStreamLocationDto? Location)> ResolveStreamLocationAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken)
    {
        MediaStreamResolveResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<
                MediaStreamResolveRequestMessage,
                MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                new MediaStreamResolveRequestMessage { MediaGuid = mediaGuid },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving media stream for cast of {MediaGuid}.", mediaGuid);
            return (StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable."), null);
        }

        if (response is null)
        {
            return (StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable."), null);
        }

        if (!response.Success || response.Item is null)
        {
            return (response.ErrorCode == "not_found"
                ? NotFound(response.ErrorMessage ?? "Media stream was not found.")
                : StatusCode(
                    StatusCodes.Status502BadGateway,
                    response.ErrorMessage ?? "Media stream lookup failed."), null);
        }

        return (null, response.Item);
    }

    internal static string VideoContentTypeOf(string storagePath)
        => Path.GetExtension(storagePath).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".ts" => "video/mp2t",
            _ => "video/mp4"
        };

    private async Task<MetadataDetailDto?> FetchMetadataAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        try
        {
            var response = await messageBus.RequestAsync<MetadataGetRequestMessage, MetadataGetResponseMessage>(
                MetadataSubjects.Get,
                new MetadataGetRequestMessage
                {
                    MediaGuid = mediaGuid,
                    OwnerSubject = AuthConstants.FindSubject(User)
                },
                QueryTimeout,
                cancellationToken);
            return response?.Success == true ? response.Item : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Metadata only enriches the receiver display; casting proceeds without it.
            logger.LogWarning(ex, "Failed fetching metadata for cast of {MediaGuid}; casting without it.", mediaGuid);
            return null;
        }
    }
}
