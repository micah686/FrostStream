using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Shared.Auth;
using Shared.Messaging;
using Shared.Storage;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

[ApiController]
[Route("stream")]
public sealed class MediaStreamController(
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    ILogger<MediaStreamController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AudioQueryTimeout = TimeSpan.FromSeconds(30);
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    /// <summary>
    /// Evaluates the caller's watch-time access to a media item via DataBridge. Returns <c>null</c> when
    /// playback is permitted, or a non-null result (403 when restricted, 503 when the check is
    /// unreachable) that the caller must return instead of serving the stream. Fails closed.
    /// </summary>
    private async Task<IActionResult?> CheckWatchAccessAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var groups = (User?.FindAll(AuthConstants.GroupsClaim) ?? [])
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        MediaAccessCheckResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<MediaAccessCheckRequestMessage, MediaAccessCheckResponseMessage>(
                MediaAccessSubjects.Check,
                new MediaAccessCheckRequestMessage { MediaGuid = mediaGuid, UserGroups = groups },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed checking media access for {MediaGuid}.", mediaGuid);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        return response.IsAllowed
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, "You are not allowed to watch this media item.");
    }

    [HttpGet("{mediaGuid:guid}")]
    [Endpoint(EndpointIds.MediaStream)]
    [EndpointSummary("Stream an archived media file")]
    [EndpointDescription("Resolves an archived media file by media GUID and streams it directly from the configured storage backend. Optional storageKey and positive version parameters select a specific stored copy; otherwise the latest matching version is used. Seekable streams support HTTP range requests for efficient playback, and the response content type is inferred from the stored file extension.")]
    public async Task<IActionResult> GetStream(
        Guid mediaGuid,
        [FromQuery] string? storageKey = null,
        [FromQuery] int? version = null,
        CancellationToken cancellationToken = default)
    {
        if (version is <= 0)
        {
            return BadRequest("Query parameter 'version' must be greater than zero.");
        }

        if (await CheckWatchAccessAsync(mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        storageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey.Trim();

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

        var location = response.Item;
        try
        {
            var storage = await blobStorageProvider.GetAsync(location.StorageKey, cancellationToken);
            var stream = await storage.OpenReadAsync(location.StoragePath, cancellationToken);
            if (stream is null)
            {
                return NotFound("The selected media stream is missing from storage.");
            }

            var contentType = ContentTypeProvider.TryGetContentType(location.StoragePath, out var resolvedContentType)
                ? resolvedContentType
                : "application/octet-stream";

            return File(stream, contentType, enableRangeProcessing: stream.CanSeek);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return NotFound("The selected media stream is missing from storage.");
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound("The selected media stream is missing from storage.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed opening media stream {MediaGuid} version {Version} from storage {StorageKey} at {StoragePath}.",
                location.MediaGuid,
                location.Version,
                location.StorageKey,
                location.StoragePath);

            return StatusCode(
                StatusCodes.Status502BadGateway,
                "The selected media stream could not be opened.");
        }
    }

    [HttpGet("audio/{mediaGuid:guid}/")]
    [Endpoint(EndpointIds.MediaAudioStream)]
    [EndpointSummary("Stream a cached audio rendition")]
    [EndpointDescription("Streams a cached audio file rendition for a media item. When the requested rendition is missing, DataBridge queues MediaProcessor work and the endpoint returns 202 while the audio is prepared.")]
    public async Task<IActionResult> GetAudioFile(
        Guid mediaGuid,
        [FromQuery] string format = "opus",
        [FromQuery] string? storageKey = null,
        [FromQuery] int? sourceVersion = null,
        [FromQuery] bool cacheAudio = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseAudioFormat(format, out var audioFormat))
            return BadRequest("Audio format must be 'aac', 'opus', or 'mp3'.");

        if (await CheckWatchAccessAsync(mediaGuid, cancellationToken) is { } denied)
            return denied;

        var response = await ResolveAudioRenditionAsync(
            mediaGuid,
            audioFormat,
            storageKey,
            sourceVersion,
            createIfMissing: cacheAudio,
            cancellationToken);

        if (response.Result is not null)
            return response.Result;

        var rendition = response.Rendition!;
        if (rendition.Status != AudioRenditionStatus.Ready || string.IsNullOrWhiteSpace(rendition.StoragePath))
        {
            return Accepted(new
            {
                rendition.RenditionId,
                rendition.MediaGuid,
                rendition.SourceVersion,
                Format = rendition.Format.ToString().ToLowerInvariant(),
                Status = rendition.Status.ToString().ToLowerInvariant()
            });
        }

        try
        {
            var storage = await blobStorageProvider.GetAsync(rendition.StorageKey, cancellationToken);
            var stream = await storage.OpenReadAsync(rendition.StoragePath, cancellationToken);
            if (stream is null)
                return NotFound("The selected audio rendition is missing from storage.");

            return File(stream, AudioContentType(rendition.Format), enableRangeProcessing: stream.CanSeek);
        }
        catch (FileNotFoundException)
        {
            return NotFound("The selected audio rendition is missing from storage.");
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound("The selected audio rendition is missing from storage.");
        }
    }

    [HttpGet("audio/{mediaGuid:guid}/index.m3u8")]
    [Endpoint(EndpointIds.MediaAudioPlaylist)]
    [EndpointSummary("Get an audio-only HLS media playlist")]
    [EndpointDescription("Returns the stored HLS media playlist for a cached audio rendition, rewriting segment URLs through FrostStream so authenticated clients can fetch the generated HLS asset.")]
    public async Task<IActionResult> GetAudioPlaylist(
        Guid mediaGuid,
        [FromQuery] string format = "aac",
        [FromQuery] string? storageKey = null,
        [FromQuery] int? sourceVersion = null,
        [FromQuery] bool cacheAudio = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseAudioFormat(format, out var audioFormat))
            return BadRequest("Audio format must be 'aac', 'opus', or 'mp3'.");

        if (await CheckWatchAccessAsync(mediaGuid, cancellationToken) is { } denied)
            return denied;

        var response = await ResolveAudioRenditionAsync(
            mediaGuid,
            audioFormat,
            storageKey,
            sourceVersion,
            createIfMissing: cacheAudio,
            cancellationToken);

        if (response.Result is not null)
            return response.Result;

        var rendition = response.Rendition!;
        if (rendition.Status != AudioRenditionStatus.Ready)
        {
            return Accepted(new
            {
                rendition.RenditionId,
                Status = rendition.Status.ToString().ToLowerInvariant()
            });
        }

        return await ServeAudioManifestAsync(rendition, cancellationToken);
    }

    [HttpGet("audio/{mediaGuid:guid}/hls/{format}/{sourceVersion:int}/{fileName}")]
    [Endpoint(EndpointIds.MediaAudioSegment)]
    [EndpointSummary("Stream an audio HLS segment")]
    [EndpointDescription("Streams a cached HLS media segment or fMP4 initialization file belonging to a prepared audio rendition. Segment paths are resolved from rendition metadata and validated so clients can fetch only files generated beside the stored manifest.")]
    public async Task<IActionResult> GetAudioSegment(
        Guid mediaGuid,
        string format,
        int sourceVersion,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseAudioFormat(format, out var audioFormat))
            return BadRequest("Audio format must be 'aac', 'opus', or 'mp3'.");

        if (!IsSafeHlsFileName(fileName))
            return BadRequest("Invalid HLS file name.");

        if (await CheckWatchAccessAsync(mediaGuid, cancellationToken) is { } denied)
            return denied;

        var response = await ResolveAudioRenditionAsync(
            mediaGuid,
            audioFormat,
            storageKey: null,
            sourceVersion,
            createIfMissing: false,
            cancellationToken);

        if (response.Result is not null)
            return response.Result;

        var rendition = response.Rendition!;
        if (rendition.Status != AudioRenditionStatus.Ready || string.IsNullOrWhiteSpace(rendition.StoragePath))
            return NotFound("The selected audio segment is not ready.");

        var segmentPath = CombineStoragePath(HlsStorageDirectory(rendition), fileName);
        try
        {
            var storage = await blobStorageProvider.GetAsync(rendition.StorageKey, cancellationToken);
            var stream = await storage.OpenReadAsync(segmentPath, cancellationToken);
            if (stream is null)
                return NotFound("The selected audio segment is missing from storage.");

            return File(stream, HlsFileContentType(fileName), enableRangeProcessing: stream.CanSeek);
        }
        catch (FileNotFoundException)
        {
            return NotFound("The selected audio segment is missing from storage.");
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound("The selected audio segment is missing from storage.");
        }
    }

    [HttpGet("playlists/{playlistId:guid}/audio.m3u8")]
    [Endpoint(EndpointIds.PlaylistAudioStream)]
    [EndpointSummary("Get an audio-only playlist stream")]
    [EndpointDescription("Returns an M3U8 playlist for the ready audio renditions in a downloaded playlist and queues missing audio renditions through MediaProcessor.")]
    public async Task<IActionResult> GetPlaylistAudio(
        Guid playlistId,
        [FromQuery] string format = "aac",
        CancellationToken cancellationToken = default)
    {
        if (!TryParseAudioFormat(format, out var audioFormat))
            return BadRequest("Audio format must be 'aac', 'opus', or 'mp3'.");

        PlaylistGetResponseMessage? playlistResponse;
        try
        {
            playlistResponse = await messageBus.RequestAsync<PlaylistGetRequestMessage, PlaylistGetResponseMessage>(
                PlaylistSubjects.PlaylistGet,
                new PlaylistGetRequestMessage { PlaylistId = playlistId },
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving playlist audio stream for {PlaylistId}.", playlistId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (playlistResponse?.Success != true || playlistResponse.Playlist is null)
            return NotFound(playlistResponse?.ErrorMessage ?? "Playlist was not found.");

        var readyManifests = new List<(AudioRenditionDto Rendition, string Manifest)>();
        foreach (var item in playlistResponse.Playlist.Items?.Where(x => x.MediaGuid is not null).OrderBy(x => x.PlaylistIndex)
                     ?? Enumerable.Empty<PlaylistItemDto>())
        {
            // Skip items the caller is not allowed to watch rather than failing the whole playlist.
            if (await CheckWatchAccessAsync(item.MediaGuid!.Value, cancellationToken) is not null)
                continue;

            var resolved = await ResolveAudioRenditionAsync(
                item.MediaGuid!.Value,
                audioFormat,
                storageKey: null,
                sourceVersion: null,
                createIfMissing: true,
                cancellationToken);

            if (resolved.Rendition?.Status == AudioRenditionStatus.Ready)
            {
                var manifest = await ReadStorageTextAsync(
                    resolved.Rendition.StorageKey,
                    HlsManifestStoragePath(resolved.Rendition),
                    cancellationToken);
                if (manifest is not null)
                    readyManifests.Add((resolved.Rendition, manifest));
            }
        }

        if (readyManifests.Count == 0)
            return Accepted(new { playlistId, Status = "preparing" });

        return Content(BuildCombinedPlaylistM3u8(readyManifests), "application/vnd.apple.mpegurl");
    }

    private async Task<IActionResult> ServeAudioManifestAsync(AudioRenditionDto rendition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rendition.StoragePath))
            return NotFound("The selected audio rendition has no HLS manifest.");

        var manifest = await ReadStorageTextAsync(rendition.StorageKey, HlsManifestStoragePath(rendition), cancellationToken);
        if (manifest is null)
            return NotFound("The selected audio HLS manifest is missing from storage.");

        return Content(RewriteManifestSegmentUris(rendition, manifest), "application/vnd.apple.mpegurl");
    }

    private async Task<string?> ReadStorageTextAsync(string storageKey, string? storagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            return null;

        try
        {
            var storage = await blobStorageProvider.GetAsync(storageKey, cancellationToken);
            await using var stream = await storage.OpenReadAsync(storagePath, cancellationToken);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<(IActionResult? Result, AudioRenditionDto? Rendition)> ResolveAudioRenditionAsync(
        Guid mediaGuid,
        AudioRenditionFormat format,
        string? storageKey,
        int? sourceVersion,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await messageBus.RequestAsync<AudioRenditionResolveRequest, AudioRenditionResolveResponse>(
                AudioRenditionSubjects.Resolve,
                new AudioRenditionResolveRequest
                {
                    MediaGuid = mediaGuid,
                    Format = format,
                    StorageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey.Trim(),
                    SourceVersion = sourceVersion,
                    CreateIfMissing = createIfMissing
                },
                AudioQueryTimeout,
                cancellationToken);

            if (response is null)
                return (StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable."), null);

            if (!response.Success)
            {
                return (response.ErrorCode == "not_found"
                    ? NotFound(response.ErrorMessage ?? "Audio rendition source was not found.")
                    : StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Audio rendition lookup failed."), null);
            }

            return response.Item is null
                ? (StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid audio rendition response."), null)
                : (null, response.Item);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving audio rendition for {MediaGuid}.", mediaGuid);
            return (StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable."), null);
        }
    }

    private static bool TryParseAudioFormat(string? value, out AudioRenditionFormat format)
    {
        format = AudioRenditionFormat.Opus;
        return value?.Trim().ToLowerInvariant() switch
        {
            "aac" or "m4a" => Set(out format, AudioRenditionFormat.Aac),
            "opus" => Set(out format, AudioRenditionFormat.Opus),
            "mp3" => Set(out format, AudioRenditionFormat.Mp3),
            _ => false
        };
    }

    private static bool Set(out AudioRenditionFormat target, AudioRenditionFormat value)
    {
        target = value;
        return true;
    }

    private static string AudioFormatToken(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Aac => "aac",
            AudioRenditionFormat.Opus => "opus",
            AudioRenditionFormat.Mp3 => "mp3",
            _ => "opus"
        };

    private static string AudioContentType(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Aac => "audio/mp4",
            AudioRenditionFormat.Opus => "audio/ogg",
            AudioRenditionFormat.Mp3 => "audio/mpeg",
            _ => "application/octet-stream"
        };

    private string RewriteManifestSegmentUris(AudioRenditionDto rendition, string manifest)
    {
        var lines = new List<string>();
        foreach (var rawLine in manifest.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                lines.Add(RewriteMapUri(rendition, line));
            }
            else if (line.Length == 0 || line.StartsWith('#'))
            {
                lines.Add(line);
            }
            else
            {
                lines.Add(BuildSegmentUrl(rendition, line));
            }
        }

        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

    private string BuildCombinedPlaylistM3u8(IEnumerable<(AudioRenditionDto Rendition, string Manifest)> manifests)
    {
        var bodyLines = new List<string>();
        var targetDuration = 10;

        foreach (var (rendition, manifest) in manifests)
        {
            foreach (var rawLine in manifest.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal) &&
                    int.TryParse(line["#EXT-X-TARGETDURATION:".Length..], out var parsedTarget))
                {
                    targetDuration = Math.Max(targetDuration, parsedTarget);
                    continue;
                }

                if (line.Length == 0 ||
                    line is "#EXTM3U" or "#EXT-X-ENDLIST" ||
                    line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-INDEPENDENT-SEGMENTS", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    bodyLines.Add(RewriteMapUri(rendition, line));
                }
                else if (line.StartsWith('#'))
                {
                    bodyLines.Add(line);
                }
                else
                {
                    bodyLines.Add(BuildSegmentUrl(rendition, line));
                }
            }
        }

        var lines = new List<string>
        {
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            "#EXT-X-PLAYLIST-TYPE:VOD",
            $"#EXT-X-TARGETDURATION:{targetDuration}"
        };

        lines.AddRange(bodyLines);
        lines.Add("#EXT-X-ENDLIST");
        return string.Join('\n', lines) + "\n";
    }

    private string RewriteMapUri(AudioRenditionDto rendition, string line)
        => RewriteQuotedUri(line, uri => BuildSegmentUrl(rendition, uri));

    private static string RewriteQuotedUri(string line, Func<string, string> rewrite)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return line;

        start += marker.Length;
        var end = line.IndexOf('"', start);
        if (end < 0)
            return line;

        var original = line[start..end];
        return line[..start] + rewrite(original) + line[end..];
    }

    private string BuildSegmentUrl(AudioRenditionDto rendition, string fileName)
    {
        fileName = Path.GetFileName(fileName.Trim());
        var uri = Url.ActionLink(
            action: nameof(GetAudioSegment),
            values: new
            {
                mediaGuid = rendition.MediaGuid,
                format = AudioFormatToken(rendition.Format),
                sourceVersion = rendition.SourceVersion,
                fileName
            });

        return uri ?? $"/stream/audio/{rendition.MediaGuid:D}/hls/{AudioFormatToken(rendition.Format)}/{rendition.SourceVersion}/{Uri.EscapeDataString(fileName)}";
    }

    private static bool IsSafeHlsFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            return false;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".ts" or ".m4s" or ".mp4";
    }

    private static string HlsFileContentType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".ts" => "video/mp2t",
            ".m4s" => "video/iso.segment",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

    private static string StorageDirectory(string storagePath)
    {
        var slash = storagePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : storagePath[..slash];
    }

    private static string CombineStoragePath(string directory, string fileName)
        => string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory.TrimEnd('/')}/{fileName}";

    private static string HlsStorageDirectory(AudioRenditionDto rendition)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? "hls"
            : CombineStoragePath(StorageDirectory(rendition.StoragePath), "hls");

    private static string? HlsManifestStoragePath(AudioRenditionDto rendition)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? null
            : CombineStoragePath(HlsStorageDirectory(rendition), "index.m3u8");
}
