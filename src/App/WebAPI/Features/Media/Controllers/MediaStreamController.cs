using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
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

    [HttpGet("audio/{mediaGuid:guid}/file.{format}")]
    [Endpoint(EndpointIds.MediaAudioStream)]
    [EndpointSummary("Stream a cached audio rendition")]
    [EndpointDescription("Streams a cached audio rendition for a media item. When the requested rendition is missing, DataBridge queues MediaProcessor work and the endpoint returns 202 while the audio is prepared.")]
    public async Task<IActionResult> GetAudioFile(
        Guid mediaGuid,
        string format,
        [FromQuery] string? storageKey = null,
        [FromQuery] int? sourceVersion = null,
        [FromQuery] bool cacheAudio = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseAudioFormat(format, out var audioFormat))
            return BadRequest("Audio format must be 'aac' or 'opus'.");

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
    [Endpoint(EndpointIds.MediaAudioStream)]
    [EndpointSummary("Get an audio-only media playlist")]
    [EndpointDescription("Returns a simple audio-only M3U8 playlist for a cached media rendition, queuing MediaProcessor work when the rendition is missing.")]
    public async Task<IActionResult> GetAudioPlaylist(
        Guid mediaGuid,
        [FromQuery] string format = "aac",
        [FromQuery] string? storageKey = null,
        [FromQuery] int? sourceVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseAudioFormat(format, out var audioFormat))
            return BadRequest("Audio format must be 'aac' or 'opus'.");

        var response = await ResolveAudioRenditionAsync(
            mediaGuid,
            audioFormat,
            storageKey,
            sourceVersion,
            createIfMissing: true,
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

        var uri = Url.ActionLink(
            action: nameof(GetAudioFile),
            values: new
            {
                mediaGuid,
                format = AudioFormatToken(audioFormat),
                storageKey = rendition.StorageKey,
                sourceVersion = rendition.SourceVersion,
                cacheAudio = true
            }) ?? $"/stream/audio/{mediaGuid:D}/file.{AudioFormatToken(audioFormat)}";

        return Content(BuildSingleItemM3u8(uri, rendition.DurationSeconds), "application/vnd.apple.mpegurl");
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
            return BadRequest("Audio format must be 'aac' or 'opus'.");

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

        var readyUris = new List<(string Uri, int? DurationSeconds)>();
        foreach (var item in playlistResponse.Playlist.Items?.Where(x => x.MediaGuid is not null).OrderBy(x => x.PlaylistIndex)
                     ?? Enumerable.Empty<PlaylistItemDto>())
        {
            var resolved = await ResolveAudioRenditionAsync(
                item.MediaGuid!.Value,
                audioFormat,
                storageKey: null,
                sourceVersion: null,
                createIfMissing: true,
                cancellationToken);

            if (resolved.Rendition?.Status == AudioRenditionStatus.Ready)
            {
                var uri = Url.ActionLink(
                    action: nameof(GetAudioFile),
                    values: new
                    {
                        mediaGuid = item.MediaGuid.Value,
                        format = AudioFormatToken(audioFormat),
                        sourceVersion = resolved.Rendition.SourceVersion,
                        cacheAudio = true
                    }) ?? $"/stream/audio/{item.MediaGuid.Value:D}/file.{AudioFormatToken(audioFormat)}";
                readyUris.Add((uri, resolved.Rendition.DurationSeconds));
            }
        }

        if (readyUris.Count == 0)
            return Accepted(new { playlistId, Status = "preparing" });

        return Content(BuildPlaylistM3u8(readyUris), "application/vnd.apple.mpegurl");
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
        format = AudioRenditionFormat.Aac;
        return value?.Trim().ToLowerInvariant() switch
        {
            "aac" or "m4a" => Set(out format, AudioRenditionFormat.Aac),
            "opus" => Set(out format, AudioRenditionFormat.Opus),
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
            _ => "aac"
        };

    private static string AudioContentType(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Aac => "audio/mp4",
            AudioRenditionFormat.Opus => "audio/ogg",
            _ => "application/octet-stream"
        };

    private static string BuildSingleItemM3u8(string uri, int? durationSeconds)
        => BuildPlaylistM3u8([(uri, durationSeconds)]);

    private static string BuildPlaylistM3u8(IEnumerable<(string Uri, int? DurationSeconds)> items)
    {
        var lines = new List<string>
        {
            "#EXTM3U",
            "#EXT-X-VERSION:3",
            "#EXT-X-PLAYLIST-TYPE:VOD",
            "#EXT-X-TARGETDURATION:600"
        };

        foreach (var (uri, durationSeconds) in items)
        {
            var duration = Math.Max(durationSeconds ?? 0, 0);
            lines.Add($"#EXTINF:{duration},");
            lines.Add(uri);
        }

        lines.Add("#EXT-X-ENDLIST");
        return string.Join('\n', lines) + "\n";
    }
}
