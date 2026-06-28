using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using FluentStorage.Blobs;
using FlySwattr.NATS.Abstractions;
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

            await RunFfmpegAsync(inputPath, outputPath, item.Format, CancellationToken.None);

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length == 0)
                throw new InvalidOperationException("ffmpeg completed without producing an audio file.");

            var outputStorage = await blobStorageProvider.GetAsync(item.OutputStorageKey, CancellationToken.None);
            await using (var output = File.OpenRead(outputPath))
            {
                await outputStorage.WriteAsync(item.OutputStoragePath, output, append: false);
            }

            var hash = await ComputeXxHash128Async(outputPath, CancellationToken.None);
            await messageBus.RequestAsync<AudioRenditionCompleteRequest, AudioRenditionCompleteResponse>(
                AudioRenditionSubjects.Complete,
                new AudioRenditionCompleteRequest
                {
                    RenditionId = item.RenditionId,
                    StoragePath = item.OutputStoragePath,
                    ContentHashXxh128 = hash,
                    SizeBytes = outputInfo.Length,
                    DurationSeconds = null
                },
                DataBridgeTimeout,
                CancellationToken.None);

            logger.LogInformation(
                "Audio rendition encode completed for {RenditionId} StorageKey {StorageKey} StoragePath {StoragePath} SizeBytes {SizeBytes}.",
                item.RenditionId,
                item.OutputStorageKey,
                item.OutputStoragePath,
                outputInfo.Length);

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

    private async Task RunFfmpegAsync(
        string inputPath,
        string outputPath,
        AudioRenditionFormat format,
        CancellationToken cancellationToken)
    {
        var args = format switch
        {
            AudioRenditionFormat.Aac => $"-hide_banner -y -i {Quote(inputPath)} -vn -c:a aac -b:a {options.Value.AacBitrate} -movflags +faststart {Quote(outputPath)}",
            AudioRenditionFormat.Opus => $"-hide_banner -y -i {Quote(inputPath)} -vn -c:a libopus -b:a {options.Value.OpusBitrate} {Quote(outputPath)}",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio rendition format.")
        };

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.Value.FfmpegPath,
                Arguments = args,
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

    private static string Extension(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Aac => "m4a",
            AudioRenditionFormat.Opus => "opus",
            _ => "bin"
        };

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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
