using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;
using Shared.Storage;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// HLS streaming surface: per-media manifests, their segments, and combined playlist streams.
/// Video manifests (the default) serve the cached H.264/AAC stream rendition prepared by
/// MediaProcessor under <c>stream/hls</c>; <c>audio=true</c> serves audio-only renditions.
/// Progressive playback lives in <see cref="MediaWatchController"/>.
/// </summary>
[ApiController]
[Route("api/media/stream")]
public sealed class MediaStreamController(
    IStoreProvider blobStorageProvider,
    MediaAccessChecker accessChecker,
    AudioRenditionResolver audioRenditions,
    StreamRenditionResolver streamRenditions,
    ILogger<MediaStreamController> logger) : ControllerBase
{
    private const string AudioKind = "audio";
    private const string VideoKind = "video";

    [HttpGet("{mediaGuid:guid}/index.m3u8")]
    [EnableCors(MediaCors.Policy)]
    [Endpoint(EndpointIds.MediaHlsManifest)]
    [EndpointSummary("Get an HLS playlist for a media item")]
    [EndpointDescription("Returns an HLS media playlist for a media item, rewriting segment URLs through FrostStream so authorized clients can fetch the generated HLS assets. By default the stream/casting-friendly H.264/AAC video rendition is served; with audio=true the opus audio-only rendition is served instead. Missing renditions are queued through MediaProcessor and the endpoint returns 202 while they are prepared; prepare=false suppresses queueing.")]
    public async Task<IActionResult> GetManifest(
        Guid mediaGuid,
        [FromQuery] bool audio = false,
        [FromQuery] string? storageKey = null,
        [FromQuery] int? sourceVersion = null,
        [FromQuery] bool prepare = true,
        CancellationToken cancellationToken = default)
    {
        if (!audio)
        {
            return await GetVideoManifestAsync(mediaGuid, storageKey, sourceVersion, prepare, cancellationToken);
        }

        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

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

        if (rendition!.Status != AudioRenditionStatus.Ready)
        {
            return Accepted(new
            {
                rendition.RenditionId,
                Status = rendition.Status.ToString().ToLowerInvariant()
            });
        }

        return await ServeAudioManifestAsync(rendition, cancellationToken);
    }

    private async Task<IActionResult> GetVideoManifestAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        bool prepare,
        CancellationToken cancellationToken)
    {
        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        var (error, rendition) = await streamRenditions.ResolveAsync(
            mediaGuid,
            storageKey,
            sourceVersion,
            createIfMissing: prepare,
            cancellationToken);

        if (error is not null)
        {
            return error;
        }

        if (rendition!.Status != StreamRenditionStatus.Ready)
        {
            return Accepted(new
            {
                rendition.RenditionId,
                Status = rendition.Status.ToString().ToLowerInvariant()
            });
        }

        var manifest = await MediaBlobServing.ReadBlobTextAsync(
            blobStorageProvider,
            rendition.StorageKey,
            StreamRenditionHelpers.HlsManifestStoragePath(rendition),
            cancellationToken);
        if (manifest is null)
        {
            return NotFound("The stream rendition HLS manifest is missing from storage.");
        }

        return Content(
            RewriteManifestUris(manifest, fileName => BuildVideoSegmentUrl(rendition, fileName)),
            StreamRenditionHelpers.HlsContentType);
    }

    [HttpGet("{mediaGuid:guid}/hls/{kind:regex(^(audio|video)$)}/{format}/{sourceVersion:int}/{fileName}")]
    [EnableCors(MediaCors.Policy)]
    [Endpoint(EndpointIds.MediaHlsSegment)]
    [EndpointSummary("Stream an HLS segment")]
    [EndpointDescription("Streams a cached HLS media segment or fMP4 initialization file belonging to a prepared rendition. Segment paths are resolved from rendition metadata and validated so clients can fetch only files generated beside the stored manifest. kind selects the video stream rendition ('video') or an audio-only rendition ('audio').")]
    public async Task<IActionResult> GetSegment(
        Guid mediaGuid,
        string kind,
        string format,
        int sourceVersion,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!AudioRenditionHelpers.IsSafeHlsFileName(fileName))
        {
            return BadRequest("Invalid HLS file name.");
        }

        if (kind == VideoKind)
        {
            return await GetVideoSegmentAsync(mediaGuid, sourceVersion, fileName, cancellationToken);
        }

        if (!string.Equals(format, AudioRenditionHelpers.FormatToken, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Audio HLS segments are only published for the opus rendition.");
        }

        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        var (error, rendition) = await audioRenditions.ResolveAsync(
            mediaGuid,
            storageKey: null,
            sourceVersion,
            createIfMissing: false,
            cancellationToken);

        if (error is not null)
        {
            return error;
        }

        if (rendition!.Status != AudioRenditionStatus.Ready || string.IsNullOrWhiteSpace(rendition.StoragePath))
        {
            return NotFound("The selected segment is not ready.");
        }

        var segmentPath = AudioRenditionHelpers.CombineStoragePath(
            AudioRenditionHelpers.HlsStorageDirectory(rendition),
            fileName);

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            rendition.StorageKey,
            segmentPath,
            subject: "HLS segment",
            contentType: AudioRenditionHelpers.HlsFileContentType(fileName),
            cancellationToken: cancellationToken);
    }

    private async Task<IActionResult> GetVideoSegmentAsync(
        Guid mediaGuid,
        int sourceVersion,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (await accessChecker.CheckWatchAccessAsync(User, mediaGuid, cancellationToken) is { } denied)
        {
            return denied;
        }

        var (error, rendition) = await streamRenditions.ResolveAsync(
            mediaGuid,
            storageKey: null,
            sourceVersion,
            createIfMissing: false,
            cancellationToken);

        if (error is not null)
        {
            return error;
        }

        if (rendition!.Status != StreamRenditionStatus.Ready ||
            StreamRenditionHelpers.SegmentStoragePath(rendition, fileName) is not { } segmentPath)
        {
            return NotFound("The selected segment is not ready.");
        }

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            rendition.StorageKey,
            segmentPath,
            subject: "HLS segment",
            contentType: AudioRenditionHelpers.HlsFileContentType(fileName),
            cancellationToken: cancellationToken);
    }

    private async Task<IActionResult> ServeAudioManifestAsync(AudioRenditionDto rendition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rendition.StoragePath))
        {
            return NotFound("The selected audio rendition has no HLS manifest.");
        }

        var manifest = await MediaBlobServing.ReadBlobTextAsync(
            blobStorageProvider,
            rendition.StorageKey,
            AudioRenditionHelpers.HlsManifestStoragePath(rendition),
            cancellationToken);
        if (manifest is null)
        {
            return NotFound("The selected audio HLS manifest is missing from storage.");
        }

        return Content(RewriteManifestSegmentUris(rendition, manifest), "application/vnd.apple.mpegurl");
    }

    private string RewriteManifestSegmentUris(AudioRenditionDto rendition, string manifest)
        => RewriteManifestUris(manifest, fileName => BuildSegmentUrl(rendition, fileName));

    private static string RewriteManifestUris(string manifest, Func<string, string> buildUrl)
    {
        var lines = new List<string>();
        foreach (var rawLine in manifest.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                lines.Add(RewriteQuotedUri(line, buildUrl));
            }
            else if (line.Length == 0 || line.StartsWith('#'))
            {
                lines.Add(line);
            }
            else
            {
                lines.Add(buildUrl(line));
            }
        }

        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

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
        => BuildSegmentUrl(
            rendition.MediaGuid,
            AudioKind,
            AudioRenditionHelpers.FormatToken,
            rendition.SourceVersion,
            fileName);

    private string BuildVideoSegmentUrl(StreamRenditionDto rendition, string fileName)
        => BuildSegmentUrl(
            rendition.MediaGuid,
            VideoKind,
            StreamRenditionHelpers.FormatToken,
            rendition.SourceVersion,
            fileName);

    private string BuildSegmentUrl(Guid mediaGuid, string kind, string formatToken, int sourceVersion, string fileName)
    {
        fileName = Path.GetFileName(fileName.Trim());

        // Sessionless clients (cast devices) authenticate every request with the manifest's cast
        // token, so segment URLs must carry it forward.
        var castToken = Request.Query[CastTokenDefaults.QueryParameter].ToString();

        var uri = Url.ActionLink(
            action: nameof(GetSegment),
            values: new
            {
                mediaGuid,
                kind,
                format = formatToken,
                sourceVersion,
                fileName
            });

        uri ??= $"/stream/{mediaGuid:D}/hls/{kind}/{formatToken}/{sourceVersion}/{Uri.EscapeDataString(fileName)}";
        return string.IsNullOrEmpty(castToken)
            ? uri
            : $"{uri}{(uri.Contains('?') ? '&' : '?')}{CastTokenDefaults.QueryParameter}={Uri.EscapeDataString(castToken)}";
    }
}
