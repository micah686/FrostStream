using Conduit.NATS;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;
using Shared.Storage;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// Progressive (non-HLS) playback surface: the original media file, an optional cached audio-only
/// rendition of it, plus the assets a player needs (thumbnail, captions, account art) and cast
/// token issuance. HLS/m3u8 streaming lives in <see cref="MediaStreamController"/>.
/// </summary>
[ApiController]
[Route("api/media/watch")]
public sealed class MediaWatchController(
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    MediaAccessChecker accessChecker,
    AudioRenditionResolver audioRenditions,
    CastTokenService castTokens,
    ILogger<MediaWatchController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [HttpGet("{mediaGuid:guid}")]
    [HttpHead("{mediaGuid:guid}")]
    [EnableCors(MediaCors.Policy)]
    [Endpoint(EndpointIds.MediaStream)]
    [EndpointSummary("Play back an archived media file")]
    [EndpointDescription("Streams an archived media file by GUID directly from the configured storage backend, with HTTP range support for seeking. Optional storageKey and positive version parameters select a specific stored copy. With audio=true the cached opus audio-only rendition is served instead; when that rendition is still being prepared the endpoint returns 202 with a status body, and prepare=false suppresses queueing missing renditions.")]
    public async Task<IActionResult> GetWatch(
        Guid mediaGuid,
        [FromQuery] bool audio = false,
        [FromQuery] bool prepare = true,
        [FromQuery] string? storageKey = null,
        [FromQuery] int? version = null,
        CancellationToken cancellationToken = default)
    {
        if (Request.Query.ContainsKey(CastTokenDefaults.QueryParameter))
        {
            logger.LogInformation(
                "Cast device requested media {MediaGuid} from {RemoteIp}; audio={Audio}, range={Range}, userAgent={UserAgent}.",
                mediaGuid,
                HttpContext.Connection.RemoteIpAddress,
                audio,
                Request.Headers.Range.ToString(),
                Request.Headers.UserAgent.ToString());
        }

        if (version is <= 0)
        {
            return BadRequest("Query parameter 'version' must be greater than zero.");
        }

        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        storageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey.Trim();

        return audio
            ? await ServeAudioRenditionAsync(mediaGuid, storageKey, version, prepare, cancellationToken)
            : await ServeOriginalFileAsync(mediaGuid, storageKey, version, cancellationToken);
    }

    private async Task<IActionResult> ServeOriginalFileAsync(
        Guid mediaGuid,
        string? storageKey,
        int? version,
        CancellationToken cancellationToken)
    {
        MediaStreamResolveResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<
                MediaStreamResolveRequestMessage,
                MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                new MediaStreamResolveRequestMessage
                {
                    MediaGuid = mediaGuid,
                    StorageKey = storageKey,
                    Version = version
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving media stream for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (!response.Success)
        {
            return response.ErrorCode == "not_found"
                ? NotFound(response.ErrorMessage ?? "Media stream was not found.")
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    response.ErrorMessage ?? "Media stream lookup failed.");
        }

        if (response.Item is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "DataBridge returned an invalid media stream response.");
        }

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            response.Item.StorageKey,
            response.Item.StoragePath,
            subject: "media stream",
            cancellationToken: cancellationToken);
    }

    private async Task<IActionResult> ServeAudioRenditionAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        bool prepare,
        CancellationToken cancellationToken)
    {
        var (error, rendition) = await audioRenditions.ResolveAsync(
            mediaGuid,
            storageKey,
            sourceVersion,
            createIfMissing: prepare,
            cancellationToken);

        if (error is not null)
        {
            return error;
        }

        if (rendition!.Status != AudioRenditionStatus.Ready || string.IsNullOrWhiteSpace(rendition.StoragePath))
        {
            return Accepted(new
            {
                rendition.RenditionId,
                rendition.MediaGuid,
                rendition.SourceVersion,
                Status = rendition.Status.ToString().ToLowerInvariant()
            });
        }

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            rendition.StorageKey,
            rendition.StoragePath,
            subject: "audio rendition",
            contentType: AudioRenditionHelpers.ContentType,
            cancellationToken: cancellationToken);
    }

    [HttpPost("{mediaGuid:guid}/cast-token")]
    [Endpoint(EndpointIds.MediaCastToken)]
    [EndpointSummary("Issue a cast token for a media item")]
    [EndpointDescription("Issues a short-lived token that grants read-only playback access to a single media item without a session, so cast devices (e.g. Chromecast) can fetch the stream, HLS manifest, and captions directly. The token snapshots the caller's identity and group memberships; all watch-time access checks still apply.")]
    public IActionResult CreateCastToken(Guid mediaGuid)
    {
        var (token, expiresAt) = castTokens.Issue(User, mediaGuid);
        return Ok(new { token, expiresAt });
    }

    [HttpGet("{mediaGuid:guid}/thumbnail")]
    [HttpHead("{mediaGuid:guid}/thumbnail")]
    [Endpoint(EndpointIds.MediaThumbnail)]
    [EndpointSummary("Get an archived media thumbnail")]
    [EndpointDescription("Resolves the stored thumbnail for an archived media item by GUID and streams it from the configured storage backend. The same watch-time access policy used for media playback is applied before the thumbnail is served.")]
    public async Task<IActionResult> GetThumbnail(
        Guid mediaGuid,
        CancellationToken cancellationToken = default)
    {
        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        MediaThumbnailResolveResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<
                MediaThumbnailResolveRequestMessage,
                MediaThumbnailResolveResponseMessage>(
                MediaStreamSubjects.ResolveThumbnail,
                new MediaThumbnailResolveRequestMessage { MediaGuid = mediaGuid },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving thumbnail for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (!response.Success)
        {
            return response.ErrorCode == "not_found"
                ? NotFound(response.ErrorMessage ?? "Thumbnail was not found.")
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    response.ErrorMessage ?? "Thumbnail lookup failed.");
        }

        if (response.Item is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "DataBridge returned an invalid thumbnail response.");
        }

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            response.Item.StorageKey,
            response.Item.StoragePath,
            subject: "thumbnail",
            enableRangeProcessing: false,
            cacheControl: "private, max-age=86400",
            cancellationToken: cancellationToken);
    }

    [HttpGet("accounts/{accountId:long}/{assetType:regex(^(avatar|banner)$)}")]
    [HttpHead("accounts/{accountId:long}/{assetType:regex(^(avatar|banner)$)}")]
    [Endpoint(EndpointIds.MediaAccountAsset)]
    [EndpointSummary("Get a creator account avatar or banner")]
    [EndpointDescription("Resolves the stored avatar or banner image for a creator account by its internal numeric identifier and streams it from the configured storage backend. Returns 404 when the account has no stored asset of the requested kind.")]
    public async Task<IActionResult> GetAccountAsset(
        long accountId,
        string assetType,
        CancellationToken cancellationToken = default)
    {
        var kind = string.Equals(assetType, "banner", StringComparison.OrdinalIgnoreCase)
            ? AccountAssetType.Banner
            : AccountAssetType.Avatar;

        AccountAssetResolveResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<
                AccountAssetResolveRequestMessage,
                AccountAssetResolveResponseMessage>(
                MediaStreamSubjects.ResolveAccountAsset,
                new AccountAssetResolveRequestMessage { AccountId = accountId, AssetType = kind },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving {AssetType} for account {AccountId}.", kind, accountId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (!response.Success)
        {
            return response.ErrorCode == "not_found"
                ? NotFound(response.ErrorMessage ?? "Account asset was not found.")
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    response.ErrorMessage ?? "Account asset lookup failed.");
        }

        if (response.Item is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "DataBridge returned an invalid account asset response.");
        }

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            response.Item.StorageKey,
            response.Item.StoragePath,
            subject: "account asset",
            enableRangeProcessing: false,
            cacheControl: "private, max-age=86400",
            cancellationToken: cancellationToken);
    }

    [HttpGet("{mediaGuid:guid}/captions")]
    [EnableCors(MediaCors.Policy)]
    [Endpoint(EndpointIds.MediaCaptions)]
    [EndpointSummary("List archived caption tracks")]
    [EndpointDescription("Lists caption tracks with durable sidecar files for a media item. This endpoint is watch-authorized and reads the caption locations from PostgreSQL, not Typesense.")]
    public async Task<IActionResult> ListCaptions(Guid mediaGuid, CancellationToken cancellationToken = default)
    {
        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
            return denied;

        MediaCaptionsListResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<MediaCaptionsListRequestMessage, MediaCaptionsListResponseMessage>(
                MediaStreamSubjects.ListCaptions,
                new MediaCaptionsListRequestMessage { MediaGuid = mediaGuid },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing captions for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Caption lookup failed.");

        return Ok(response.Items.Select(caption =>
        {
            var url = $"/api/media/watch/{mediaGuid:D}/captions/{Uri.EscapeDataString(caption.LanguageCode)}?captionType={Uri.EscapeDataString(caption.CaptionType)}";
            var format = Path.GetExtension(caption.StoragePath).TrimStart('.').ToLowerInvariant();
            var requiresAssRenderer = format is "ass" or "ssa";
            return new CaptionTrackResponse(
                caption.LanguageCode,
                caption.CaptionType,
                caption.Name,
                format,
                requiresAssRenderer ? "jassub" : "native",
                url,
                requiresAssRenderer ? $"{url}&raw=true" : null);
        }));
    }

    [HttpGet("{mediaGuid:guid}/captions/{languageCode}")]
    [EnableCors(MediaCors.Policy)]
    [Endpoint(EndpointIds.MediaCaption)]
    [EndpointSummary("Stream an archived caption track")]
    [EndpointDescription("Resolves a stored caption file for an archived media item by GUID and two-letter language code, applying the same watch-time access policy as media playback. The optional captionType parameter ('subtitles' or 'automatic_captions') pins a specific track; otherwise manual subtitles are preferred. SubRip files are converted to WebVTT on the fly so browsers can render them as text tracks.")]
    public async Task<IActionResult> GetCaption(
        Guid mediaGuid,
        string languageCode,
        [FromQuery] string? captionType = null,
        [FromQuery] bool raw = false,
        CancellationToken cancellationToken = default)
    {
        languageCode = languageCode.Trim();
        if (languageCode.Length == 0)
        {
            return BadRequest("Route parameter 'languageCode' is required.");
        }

        captionType = string.IsNullOrWhiteSpace(captionType) ? null : captionType.Trim();
        if (captionType is not (null or "subtitles" or "automatic_captions"))
        {
            return BadRequest("Query parameter 'captionType' must be 'subtitles' or 'automatic_captions'.");
        }

        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        MediaCaptionResolveResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<
                MediaCaptionResolveRequestMessage,
                MediaCaptionResolveResponseMessage>(
                MediaStreamSubjects.ResolveCaption,
                new MediaCaptionResolveRequestMessage
                {
                    MediaGuid = mediaGuid,
                    LanguageCode = languageCode,
                    CaptionType = captionType
                },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving caption for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (!response.Success)
        {
            return response.ErrorCode == "not_found"
                ? NotFound(response.ErrorMessage ?? "Caption track was not found.")
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    response.ErrorMessage ?? "Caption lookup failed.");
        }

        if (response.Item is null)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "DataBridge returned an invalid caption response.");
        }

        var location = response.Item;
        var extension = Path.GetExtension(location.StoragePath).ToLowerInvariant();

        if (raw)
        {
            return await this.ServeBlobAsync(
                blobStorageProvider,
                logger,
                location.StorageKey,
                location.StoragePath,
                subject: "caption track",
                contentType: extension switch
                {
                    ".ass" or ".ssa" => "text/x-ssa; charset=utf-8",
                    ".srt" => "application/x-subrip; charset=utf-8",
                    _ => null
                },
                enableRangeProcessing: false,
                cacheControl: "private, max-age=86400",
                cancellationToken: cancellationToken);
        }

        if (extension is ".srt" or ".ass" or ".ssa")
        {
            var captionText = await MediaBlobServing.ReadBlobTextAsync(
                blobStorageProvider,
                location.StorageKey,
                location.StoragePath,
                cancellationToken);
            if (captionText is null)
            {
                return NotFound("The selected caption track is missing from storage.");
            }

            Response.Headers.CacheControl = "private, max-age=86400";
            var vtt = extension == ".srt" ? ConvertSrtToWebVtt(captionText) : ConvertAssToWebVtt(captionText);
            return Content(vtt, "text/vtt; charset=utf-8");
        }

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            location.StorageKey,
            location.StoragePath,
            subject: "caption track",
            contentType: extension == ".vtt" ? "text/vtt; charset=utf-8" : null,
            enableRangeProcessing: false,
            cacheControl: "private, max-age=86400",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Minimal SubRip → WebVTT conversion: prepends the WEBVTT header and switches the
    /// millisecond separator in cue timings from comma to dot. Cue numbers are valid VTT
    /// cue identifiers, so the rest of the file passes through untouched.
    /// </summary>
    private static string ConvertSrtToWebVtt(string srt)
    {
        var lines = srt.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("-->", StringComparison.Ordinal))
            {
                lines[i] = lines[i].Replace(',', '.');
            }
        }

        return "WEBVTT\n\n" + string.Join('\n', lines);
    }

    /// <summary>Converts ASS/SSA dialogue cues to the browser-native WebVTT format. Styling is
    /// intentionally discarded; the durable source file remains unchanged.</summary>
    private static string ConvertAssToWebVtt(string ass)
    {
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var rawLine in ass.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n'))
        {
            if (!rawLine.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = rawLine["Dialogue:".Length..].Split(',', 10);
            if (parts.Length != 10)
                continue;

            var start = ConvertAssTimestamp(parts[1]);
            var end = ConvertAssTimestamp(parts[2]);
            if (start is null || end is null)
                continue;

            var text = Regex.Replace(parts[9], @"\{[^}]*\}", string.Empty)
                .Replace("\\N", "\n", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Trim();
            if (text.Length == 0)
                continue;

            output.Append(start).Append(" --> ").Append(end).Append('\n')
                .Append(text).Append("\n\n");
        }

        return output.ToString();
    }

    private static string? ConvertAssTimestamp(string value)
    {
        var parts = value.Trim().Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var hours) ||
            !int.TryParse(parts[1], out var minutes))
            return null;

        var secondsParts = parts[2].Split('.', 2);
        if (!int.TryParse(secondsParts[0], out var seconds))
            return null;

        var centiseconds = 0;
        if (secondsParts.Length == 2 && !int.TryParse(secondsParts[1], out centiseconds))
            return null;

        var milliseconds = centiseconds * 10;
        return $"{hours:00}:{minutes:00}:{seconds:00}.{milliseconds:000}";
    }

    private sealed record CaptionTrackResponse(
        string LanguageCode,
        string CaptionType,
        string? Name,
        string Format,
        string Renderer,
        string Url,
        string? SourceUrl);
}
