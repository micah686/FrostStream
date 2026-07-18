using Conduit.NATS;
using MediaProcessor.Ffmpeg;
using MediaProcessor.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Messaging;

namespace MediaProcessor.Audio;

/// <summary>
/// Produces the cached opus audio-only rendition of a stored media version, written beside the
/// original as <c>archives/&lt;guid&gt;/v&lt;n&gt;/stream/audio/media.opus</c> together with an HLS
/// packaging of the same track under <c>stream/audio/hls</c>.
/// </summary>
public sealed class AudioRenditionProcessorService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    MediaProcessorStorageClient storageClient,
    FfmpegRunner ffmpeg,
    IOptions<MediaProcessorOptions> options,
    ILogger<AudioRenditionProcessorService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);
    private static readonly TimeSpan DataBridgeTimeout = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<AudioRenditionEncodeRequested>(
            stream: Stream,
            consumer: ConsumerName.From(BackgroundJobsTopology.MediaProcessorAudioRenditionConsumer),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<AudioRenditionEncodeRequested> context)
    {
        var request = context.Message;
        var workRoot = Path.Combine(options.Value.TempRoot, "audio-" + request.RenditionId.ToString("N"));
        var inputPath = Path.Combine(workRoot, "source");
        var outputPath = Path.Combine(workRoot, "media.opus");
        var hlsRoot = Path.Combine(workRoot, "hls");
        var manifestPath = Path.Combine(hlsRoot, "index.m3u8");

        // The encode outlives the consumer ack window, so keep telling JetStream we are alive
        // instead of holding a long ack wait on the consumer.
        await using var heartbeat = new JetStreamHeartbeat(context, logger);

        try
        {
            Directory.CreateDirectory(workRoot);

            var claim = await messageBus.RequestAsync<AudioRenditionClaimRequest, AudioRenditionClaimResponse>(
                AudioRenditionSubjects.Claim,
                new AudioRenditionClaimRequest { RenditionId = request.RenditionId },
                DataBridgeTimeout,
                CancellationToken.None);

            if (claim?.Success != true || claim.Item is null)
            {
                logger.LogInformation("Skipping audio rendition {RenditionId}; it is not claimable.", request.RenditionId);
                await CompleteAsync(heartbeat, context);
                return;
            }

            var item = claim.Item;
            logger.LogInformation(
                "Audio rendition encode started for {RenditionId} MediaGuid {MediaGuid} Version {Version}.",
                item.RenditionId,
                item.MediaGuid,
                item.SourceVersion);

            await storageClient.DownloadToFileAsync(item.SourceStorageKey, item.SourceStoragePath, inputPath, CancellationToken.None);

            await ffmpeg.RunFfmpegAsync(
                $"-hide_banner -y -i {FfmpegRunner.Quote(inputPath)} -vn -c:a libopus -b:a {options.Value.OpusBitrate} {FfmpegRunner.Quote(outputPath)}",
                workingDirectory: null,
                CancellationToken.None);

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length == 0)
                throw new InvalidOperationException("ffmpeg completed without producing an audio file.");

            Directory.CreateDirectory(hlsRoot);
            await ffmpeg.RunFfmpegAsync(BuildHlsArgs(outputPath, hlsRoot, manifestPath), hlsRoot, CancellationToken.None);

            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length == 0)
                throw new InvalidOperationException("ffmpeg completed without producing an HLS manifest.");

            await storageClient.UploadFromFileAsync(outputPath, item.OutputStorageKey, item.OutputStoragePath, CancellationToken.None);

            var outputBasePath = StorageDirectory(item.OutputStoragePath);
            var hlsBasePath = CombineStoragePath(outputBasePath, "hls");
            foreach (var file in Directory.EnumerateFiles(hlsRoot))
            {
                var storagePath = CombineStoragePath(hlsBasePath, Path.GetFileName(file));
                await storageClient.UploadFromFileAsync(file, item.OutputStorageKey, storagePath, CancellationToken.None);
            }

            var hash = await FfmpegRunner.ComputeXxHash128Async(outputPath, CancellationToken.None);
            var totalSizeBytes = Directory.EnumerateFiles(hlsRoot)
                .Select(path => new FileInfo(path).Length)
                .Sum() + outputInfo.Length;
            await messageBus.RequestAsync<AudioRenditionCompleteRequest, AudioRenditionCompleteResponse>(
                AudioRenditionSubjects.Complete,
                new AudioRenditionCompleteRequest
                {
                    RenditionId = item.RenditionId,
                    StoragePath = item.OutputStoragePath,
                    ContentHashXxh128 = hash,
                    SizeBytes = totalSizeBytes,
                    DurationSeconds = null
                },
                DataBridgeTimeout,
                CancellationToken.None);

            logger.LogInformation(
                "Audio rendition encode completed for {RenditionId} StorageKey {StorageKey} StoragePath {StoragePath} SizeBytes {SizeBytes}.",
                item.RenditionId,
                item.OutputStorageKey,
                item.OutputStoragePath,
                totalSizeBytes);

            await CompleteAsync(heartbeat, context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audio rendition encode failed for {RenditionId}.", request.RenditionId);
            try
            {
                await messageBus.RequestAsync<AudioRenditionFailRequest, AudioRenditionFailResponse>(
                    AudioRenditionSubjects.Fail,
                    new AudioRenditionFailRequest { RenditionId = request.RenditionId, ErrorMessage = ex.Message },
                    DataBridgeTimeout,
                    CancellationToken.None);
            }
            catch (Exception failEx)
            {
                logger.LogWarning(failEx, "Failed recording audio rendition failure for {RenditionId}.", request.RenditionId);
            }

            await CompleteAsync(heartbeat, context);
        }
        finally
        {
            FfmpegRunner.TryDeleteDirectory(workRoot);
        }
    }

    private static async Task CompleteAsync(JetStreamHeartbeat heartbeat, IJsMessageContext<AudioRenditionEncodeRequested> context)
    {
        await heartbeat.StopAsync();
        await context.AckAsync();
    }

    private string BuildHlsArgs(string inputPath, string hlsRoot, string manifestPath)
        => string.Join(' ',
            "-hide_banner -y",
            $"-i {FfmpegRunner.Quote(inputPath)}",
            "-vn -c:a libopus",
            $"-b:a {options.Value.OpusBitrate}",
            "-f hls",
            "-hls_segment_type fmp4",
            "-hls_fmp4_init_filename init.mp4",
            $"-hls_time {Math.Max(2, options.Value.HlsSegmentSeconds)}",
            "-hls_playlist_type vod",
            $"-hls_segment_filename {FfmpegRunner.Quote(Path.Combine(hlsRoot, "segment_%05d.m4s"))}",
            FfmpegRunner.Quote(manifestPath));

    private static string StorageDirectory(string storagePath)
    {
        var slash = storagePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : storagePath[..slash];
    }

    private static string CombineStoragePath(string directory, string fileName)
        => string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory.TrimEnd('/')}/{fileName}";
}
