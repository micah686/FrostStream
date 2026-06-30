using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using FluentStorage.Blobs;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Messaging;
using Shared.Storage;

namespace MediaProcessor;

public sealed class MediaProcessorOptions
{
    public const string SectionName = "MediaProcessor";

    public string FfmpegPath { get; init; } = "ffmpeg";

    public string TempRoot { get; init; } = Path.Combine(Path.GetTempPath(), "froststream", "mediaprocessor");

    public string AacBitrate { get; init; } = "128k";

    public string OpusBitrate { get; init; } = "96k";

    public string Mp3Bitrate { get; init; } = "128k";

    public int HlsSegmentSeconds { get; init; } = 10;
}

public sealed class AudioRenditionProcessorService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
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
        var workRoot = Path.Combine(options.Value.TempRoot, request.RenditionId.ToString("N"));
        var inputPath = Path.Combine(workRoot, "source");
        var outputPath = Path.Combine(workRoot, $"audio.{Extension(request.Format)}");
        var hlsRoot = Path.Combine(workRoot, "hls");
        var manifestPath = Path.Combine(hlsRoot, "index.m3u8");

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
                await context.AckAsync();
                return;
            }

            var item = claim.Item;
            logger.LogInformation(
                "Audio rendition encode started for {RenditionId} MediaGuid {MediaGuid} Version {Version} Format {Format}.",
                item.RenditionId,
                item.MediaGuid,
                item.SourceVersion,
                item.Format);

            var sourceStorage = await blobStorageProvider.GetAsync(item.SourceStorageKey, CancellationToken.None);
            await using (var source = await sourceStorage.OpenReadAsync(item.SourceStoragePath, CancellationToken.None)
                                      ?? throw new FileNotFoundException("Source media was not found in storage.", item.SourceStoragePath))
            await using (var destination = File.Create(inputPath))
            {
                await source.CopyToAsync(destination, CancellationToken.None);
            }

            await RunAudioFfmpegAsync(inputPath, outputPath, item.Format, CancellationToken.None);

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length == 0)
                throw new InvalidOperationException("ffmpeg completed without producing an audio file.");

            Directory.CreateDirectory(hlsRoot);
            await RunHlsFfmpegAsync(outputPath, hlsRoot, manifestPath, item.Format, CancellationToken.None);

            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length == 0)
                throw new InvalidOperationException("ffmpeg completed without producing an HLS manifest.");

            var outputStorage = await blobStorageProvider.GetAsync(item.OutputStorageKey, CancellationToken.None);
            await using (var output = File.OpenRead(outputPath))
            {
                await outputStorage.WriteAsync(item.OutputStoragePath, output, append: false);
            }

            var outputBasePath = StorageDirectory(item.OutputStoragePath);
            var hlsBasePath = CombineStoragePath(outputBasePath, "hls");
            foreach (var file in Directory.EnumerateFiles(hlsRoot))
            {
                var storagePath = CombineStoragePath(hlsBasePath, Path.GetFileName(file));
                await using var output = File.OpenRead(file);
                await outputStorage.WriteAsync(storagePath, output, append: false);
            }

            var hash = await ComputeXxHash128Async(outputPath, CancellationToken.None);
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

            await context.AckAsync();
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

            await context.AckAsync();
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    private async Task RunAudioFfmpegAsync(
        string inputPath,
        string outputPath,
        AudioRenditionFormat format,
        CancellationToken cancellationToken)
    {
        var args = format switch
        {
            AudioRenditionFormat.Aac => $"-hide_banner -y -i {Quote(inputPath)} -vn -c:a aac -b:a {options.Value.AacBitrate} -movflags +faststart {Quote(outputPath)}",
            AudioRenditionFormat.Opus => $"-hide_banner -y -i {Quote(inputPath)} -vn -c:a libopus -b:a {options.Value.OpusBitrate} {Quote(outputPath)}",
            AudioRenditionFormat.Mp3 => $"-hide_banner -y -i {Quote(inputPath)} -vn -c:a libmp3lame -b:a {options.Value.Mp3Bitrate} {Quote(outputPath)}",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio rendition format.")
        };

        await RunFfmpegProcessAsync(args, workingDirectory: null, cancellationToken);
    }

    private async Task RunHlsFfmpegAsync(
        string inputPath,
        string hlsRoot,
        string manifestPath,
        AudioRenditionFormat format,
        CancellationToken cancellationToken)
    {
        var args = format switch
        {
            AudioRenditionFormat.Aac => string.Join(' ',
                "-hide_banner -y",
                $"-i {Quote(inputPath)}",
                "-vn -c:a aac",
                $"-b:a {options.Value.AacBitrate}",
                "-f hls",
                $"-hls_time {Math.Max(2, options.Value.HlsSegmentSeconds)}",
                "-hls_playlist_type vod",
                $"-hls_segment_filename {Quote(Path.Combine(hlsRoot, "segment_%05d.ts"))}",
                Quote(manifestPath)),
            AudioRenditionFormat.Opus => string.Join(' ',
                "-hide_banner -y",
                $"-i {Quote(inputPath)}",
                "-vn -c:a libopus",
                $"-b:a {options.Value.OpusBitrate}",
                "-f hls",
                "-hls_segment_type fmp4",
                "-hls_fmp4_init_filename init.mp4",
                $"-hls_time {Math.Max(2, options.Value.HlsSegmentSeconds)}",
                "-hls_playlist_type vod",
                $"-hls_segment_filename {Quote(Path.Combine(hlsRoot, "segment_%05d.m4s"))}",
                Quote(manifestPath)),
            AudioRenditionFormat.Mp3 => string.Join(' ',
                "-hide_banner -y",
                $"-i {Quote(inputPath)}",
                "-vn -c:a libmp3lame",
                $"-b:a {options.Value.Mp3Bitrate}",
                "-f hls",
                $"-hls_time {Math.Max(2, options.Value.HlsSegmentSeconds)}",
                "-hls_playlist_type vod",
                $"-hls_segment_filename {Quote(Path.Combine(hlsRoot, "segment_%05d.ts"))}",
                Quote(manifestPath)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio rendition format.")
        };

        await RunFfmpegProcessAsync(args, hlsRoot, cancellationToken);
    }

    private async Task RunFfmpegProcessAsync(string args, string? workingDirectory, CancellationToken cancellationToken)
    {

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.Value.FfmpegPath,
                Arguments = args,
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            },
            EnableRaisingEvents = true
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {LastLines(stderr, 8)}");
    }

    private static async Task<string> ComputeXxHash128Async(string path, CancellationToken cancellationToken)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await using var stream = File.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }

            Span<byte> hash = stackalloc byte[16];
            hasher.GetCurrentHash(hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Extension(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Aac => "m4a",
            AudioRenditionFormat.Opus => "opus",
            AudioRenditionFormat.Mp3 => "mp3",
            _ => "bin"
        };

    private static string StorageDirectory(string storagePath)
    {
        var slash = storagePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : storagePath[..slash];
    }

    private static string CombineStoragePath(string directory, string fileName)
        => string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory.TrimEnd('/')}/{fileName}";

    private static string LastLines(string value, int count)
        => string.Join('\n', value.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort; stale scratch is safe to remove on next maintenance pass.
        }
    }
}
