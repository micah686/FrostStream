using Conduit.NATS;
using MediaProcessor.Ffmpeg;
using MediaProcessor.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Messaging;

namespace MediaProcessor.Video;

/// <summary>
/// Produces the stream/casting rendition of a stored media version: H.264 + AAC HLS segments
/// written beside the original at <c>archives/&lt;guid&gt;/v&lt;n&gt;/stream/hls</c>. Sources whose
/// tracks are already H.264/AAC are remuxed (<c>-c copy</c>) instead of re-encoded.
/// </summary>
public sealed class StreamRenditionProcessorService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    MediaProcessorStorageClient storageClient,
    FfmpegRunner ffmpeg,
    IOptions<MediaProcessorOptions> options,
    IClock clock,
    ILogger<StreamRenditionProcessorService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);
    private static readonly TimeSpan DataBridgeTimeout = TimeSpan.FromSeconds(30);

    private const string ManifestFileName = "index.m3u8";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<StreamRenditionEncodeRequested>(
            stream: Stream,
            consumer: ConsumerName.From(BackgroundJobsTopology.MediaProcessorStreamRenditionConsumer),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<StreamRenditionEncodeRequested> context)
    {
        var request = context.Message;
        var workRoot = Path.Combine(options.Value.TempRoot, "hls-" + request.RenditionId.ToString("N"));
        var inputPath = Path.Combine(workRoot, "source");
        var hlsRoot = Path.Combine(workRoot, "hls");

        // Transcodes run for as long as the source demands; heartbeat instead of a long ack wait.
        await using var heartbeat = new JetStreamHeartbeat(context, logger);
        RenditionProgressReporter? progress = null;

        try
        {
            Directory.CreateDirectory(workRoot);

            var claim = await messageBus.RequestAsync<StreamRenditionClaimRequest, StreamRenditionClaimResponse>(
                StreamRenditionSubjects.Claim,
                new StreamRenditionClaimRequest { RenditionId = request.RenditionId },
                DataBridgeTimeout,
                CancellationToken.None);

            if (claim?.Success != true || claim.Item is null)
            {
                logger.LogInformation("Skipping stream rendition {RenditionId}; it is not claimable.", request.RenditionId);
                await CompleteAsync(heartbeat, context);
                return;
            }

            var item = claim.Item;
            progress = new RenditionProgressReporter(messageBus, clock, logger, item.RenditionId, RenditionKind.Stream, item.MediaGuid);
            logger.LogInformation(
                "Stream rendition encode started for {RenditionId} MediaGuid {MediaGuid} Version {Version}.",
                item.RenditionId,
                item.MediaGuid,
                item.SourceVersion);

            await progress.PhaseAsync(RenditionProgressPhases.FetchingSource);
            await storageClient.DownloadToFileAsync(item.SourceStorageKey, item.SourceStoragePath, inputPath, CancellationToken.None);

            await progress.PhaseAsync(RenditionProgressPhases.Probing);
            var probe = await ffmpeg.ProbeAsync(inputPath, CancellationToken.None);
            if (!probe.HasVideo)
                throw new InvalidOperationException("Source media has no video track; request an audio rendition instead.");

            Directory.CreateDirectory(hlsRoot);
            var reporter = progress;
            await ffmpeg.RunFfmpegAsync(
                BuildHlsArgs(inputPath, hlsRoot, probe),
                hlsRoot,
                frame => reporter.ReportFfmpeg(RenditionProgressPhases.Encoding, frame, probe.DurationSeconds),
                CancellationToken.None);

            var manifestPath = Path.Combine(hlsRoot, ManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length == 0)
                throw new InvalidOperationException("ffmpeg completed without producing an HLS manifest.");

            var outputBase = item.OutputStoragePath.TrimEnd('/');
            long totalSizeBytes = 0;

            // Manifest goes up last so its presence in storage implies a complete segment set.
            var uploadFiles = Directory.EnumerateFiles(hlsRoot)
                .OrderBy(path => Path.GetFileName(path) == ManifestFileName ? 1 : 0)
                .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                .ToList();

            await progress.PhaseAsync(RenditionProgressPhases.Uploading, percent: 0);
            for (var i = 0; i < uploadFiles.Count; i++)
            {
                var file = uploadFiles[i];
                totalSizeBytes += new FileInfo(file).Length;
                await storageClient.UploadFromFileAsync(file, item.OutputStorageKey, $"{outputBase}/{Path.GetFileName(file)}", CancellationToken.None);
                await progress.PhaseAsync(RenditionProgressPhases.Uploading, percent: (i + 1) * 100d / uploadFiles.Count);
            }

            await messageBus.RequestAsync<StreamRenditionCompleteRequest, StreamRenditionCompleteResponse>(
                StreamRenditionSubjects.Complete,
                new StreamRenditionCompleteRequest
                {
                    RenditionId = item.RenditionId,
                    StoragePath = outputBase,
                    SizeBytes = totalSizeBytes,
                    DurationSeconds = probe.DurationSeconds
                },
                DataBridgeTimeout,
                CancellationToken.None);

            logger.LogInformation(
                "Stream rendition encode completed for {RenditionId} StorageKey {StorageKey} StoragePath {StoragePath} SizeBytes {SizeBytes} (video: {VideoCodec}, audio: {AudioCodec}).",
                item.RenditionId,
                item.OutputStorageKey,
                outputBase,
                totalSizeBytes,
                probe.VideoCodec,
                probe.AudioCodec ?? "none");

            await progress.PhaseAsync(RenditionProgressPhases.Ready, percent: 100);
            await CompleteAsync(heartbeat, context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stream rendition encode failed for {RenditionId}.", request.RenditionId);
            if (progress is not null)
                await progress.PhaseAsync(RenditionProgressPhases.Failed, message: ex.Message);
            try
            {
                await messageBus.RequestAsync<StreamRenditionFailRequest, StreamRenditionFailResponse>(
                    StreamRenditionSubjects.Fail,
                    new StreamRenditionFailRequest { RenditionId = request.RenditionId, ErrorMessage = ex.Message },
                    DataBridgeTimeout,
                    CancellationToken.None);
            }
            catch (Exception failEx)
            {
                logger.LogWarning(failEx, "Failed recording stream rendition failure for {RenditionId}.", request.RenditionId);
            }

            await CompleteAsync(heartbeat, context);
        }
        finally
        {
            FfmpegRunner.TryDeleteDirectory(workRoot);
        }
    }

    private static async Task CompleteAsync(JetStreamHeartbeat heartbeat, IJsMessageContext<StreamRenditionEncodeRequested> context)
    {
        await heartbeat.StopAsync();
        await context.AckAsync();
    }

    private string BuildHlsArgs(string inputPath, string hlsRoot, MediaProbe probe)
    {
        var opts = options.Value;
        var segmentSeconds = Math.Max(2, opts.HlsSegmentSeconds);

        // -map 0:V excludes attached-picture "video" streams (cover art).
        var args = new List<string>
        {
            "-hide_banner -y",
            $"-i {FfmpegRunner.Quote(inputPath)}",
            "-map 0:V:0",
            "-sn -dn"
        };

        if (probe.VideoCodec == "h264")
        {
            args.Add("-c:v copy");
        }
        else
        {
            args.Add($"-c:v libx264 -preset {opts.VideoPreset} -crf {opts.VideoCrf} -profile:v high -pix_fmt yuv420p");
            if (probe.VideoHeight is { } height && height > opts.VideoMaxHeight)
                args.Add($"-vf scale=-2:{opts.VideoMaxHeight}");

            // Keyframes on segment boundaries so hls_time is honored.
            args.Add($"-force_key_frames expr:gte(t,n_forced*{segmentSeconds})");
        }

        if (probe.HasAudio)
        {
            args.Add("-map 0:a:0");
            args.Add(probe.AudioCodec == "aac"
                ? "-c:a copy"
                : $"-c:a aac -b:a {opts.AacBitrate} -ac 2");
        }

        args.Add("-f hls");
        args.Add($"-hls_time {segmentSeconds}");
        args.Add("-hls_playlist_type vod");
        args.Add("-hls_flags independent_segments");
        args.Add($"-hls_segment_filename {FfmpegRunner.Quote(Path.Combine(hlsRoot, "segment_%05d.ts"))}");
        args.Add(FfmpegRunner.Quote(Path.Combine(hlsRoot, ManifestFileName)));

        return string.Join(' ', args);
    }
}
