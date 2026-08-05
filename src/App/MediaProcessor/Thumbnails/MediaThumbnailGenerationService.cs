using System.Globalization;
using Conduit.NATS;
using MediaProcessor.Ffmpeg;
using MediaProcessor.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Messaging;

namespace MediaProcessor.Thumbnails;

public sealed class MediaThumbnailGenerationService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    MediaProcessorStorageClient storageClient,
    FfmpegRunner ffmpeg,
    IOptions<MediaProcessorOptions> options,
    IBackgroundRunReporter runReporter,
    ILogger<MediaThumbnailGenerationService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);
    private static readonly TimeSpan DataBridgeTimeout = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<GenerateMissingMediaThumbnailsRequested>(
            stream: Stream,
            consumer: ConsumerName.From(BackgroundJobsTopology.MediaProcessorThumbnailGenerationConsumer),
            handler: context => HandleAsync(context, stoppingToken),
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(
        IJsMessageContext<GenerateMissingMediaThumbnailsRequested> context,
        CancellationToken cancellationToken)
    {
        await using var heartbeat = new JetStreamHeartbeat(context, logger);
        await using var run = await runReporter.BeginAsync(
            context.Message.TaskType,
            context.Message,
            "missing thumbnails",
            cancellationToken);

        var processed = 0;
        var generated = 0;
        var failed = 0;
        Guid? afterMediaGuid = null;

        try
        {
            while (true)
            {
                var page = await messageBus.RequestAsync<MissingMediaThumbnailsRequest, MissingMediaThumbnailsResponse>(
                    MediaThumbnailGenerationSubjects.ListMissing,
                    new MissingMediaThumbnailsRequest
                    {
                        AccountId = context.Message.AccountId,
                        AfterMediaGuid = afterMediaGuid,
                        Limit = 100
                    },
                    DataBridgeTimeout,
                    cancellationToken);

                if (page is not { Success: true })
                    throw new InvalidOperationException(page?.ErrorMessage ?? "DataBridge did not return thumbnail candidates.");
                if (page.Items.Count == 0)
                    break;

                foreach (var item in page.Items)
                {
                    afterMediaGuid = item.MediaGuid;
                    await run.ReportAsync($"Generating thumbnail for {item.MediaGuid}…", processed);
                    try
                    {
                        await GenerateAsync(item, cancellationToken);
                        generated++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        logger.LogWarning(ex, "Could not generate thumbnail for {MediaGuid}.", item.MediaGuid);
                        await run.ReportAsync($"Skipped {item.MediaGuid}: {ex.Message}", processed);
                    }

                    processed++;
                    await run.ReportAsync(
                        $"Generated {generated} thumbnail(s); {failed} failed.",
                        processed);
                }
            }

            run.Succeed($"Generated {generated} thumbnail(s); {failed} failed.");
            await heartbeat.StopAsync();
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            run.Fail(ex.Message);
            logger.LogError(ex, "Thumbnail generation failed for account {AccountId}.", context.Message.AccountId);
            await heartbeat.StopAsync();
            await context.NackAsync();
        }
    }

    private async Task GenerateAsync(MissingMediaThumbnailItem item, CancellationToken cancellationToken)
    {
        var workRoot = Path.Combine(options.Value.TempRoot, "thumbnail-" + item.MediaGuid.ToString("N"));
        var inputPath = Path.Combine(workRoot, "source");
        var outputPath = Path.Combine(workRoot, "thumbnail.jpg");

        try
        {
            Directory.CreateDirectory(workRoot);
            await storageClient.DownloadToFileAsync(
                item.StorageKey,
                item.StoragePath,
                inputPath,
                cancellationToken);

            var probe = await ffmpeg.ProbeAsync(inputPath, cancellationToken);
            if (!probe.HasVideo)
                throw new InvalidOperationException("Source has no video frame to use as a thumbnail.");

            var duration = probe.DurationSeconds ?? 0;
            var seekSeconds = duration <= 1 ? 0 : Math.Min(duration - 1, duration * 0.1);
            var seek = seekSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            await ffmpeg.RunFfmpegAsync(
                $"-hide_banner -y -ss {seek} -i {FfmpegRunner.Quote(inputPath)} -map 0:v:0 -frames:v 1 -vf scale=1280:-2 -q:v 2 {FfmpegRunner.Quote(outputPath)}",
                workRoot,
                cancellationToken);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("ffmpeg did not produce a thumbnail.");

            var generatedStoragePath = GeneratedThumbnailPath(item.StoragePath);
            await storageClient.UploadFromFileAsync(
                outputPath,
                item.StorageKey,
                generatedStoragePath,
                cancellationToken);

            var response = await messageBus.RequestAsync<MediaThumbnailGeneratedRequest, MediaThumbnailGeneratedResponse>(
                MediaThumbnailGenerationSubjects.Complete,
                new MediaThumbnailGeneratedRequest
                {
                    MediaGuid = item.MediaGuid,
                    StorageKey = item.StorageKey,
                    StoragePath = generatedStoragePath
                },
                DataBridgeTimeout,
                cancellationToken);

            if (response is not { Success: true })
                throw new InvalidOperationException(response?.ErrorMessage ?? "DataBridge did not attach the generated thumbnail.");
        }
        finally
        {
            FfmpegRunner.TryDeleteDirectory(workRoot);
        }
    }

    private static string GeneratedThumbnailPath(string sourceStoragePath)
    {
        var slash = sourceStoragePath.LastIndexOf('/');
        var dot = sourceStoragePath.LastIndexOf('.');
        if (dot <= slash)
            dot = sourceStoragePath.Length;
        return sourceStoragePath[..dot] + ".generated-thumbnail.jpg";
    }
}
