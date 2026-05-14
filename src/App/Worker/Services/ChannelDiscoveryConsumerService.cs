using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using YtDlpSharpLib;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;

namespace Worker.Services;

public sealed class ChannelDiscoveryConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IYtDlpClient ytDlp,
    IClock clock,
    ILogger<ChannelDiscoveryConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            Consume<ChannelUpdateCheckRequested>(
                BackgroundJobsTopology.WorkerChannelUpdateCheckConsumer,
                message => HandleScheduledScanAsync(message, CreatorSourceScanMode.Incremental, stoppingToken),
                stoppingToken),
            Consume<ChannelMediaListRequested>(
                BackgroundJobsTopology.WorkerChannelMediaListConsumer,
                message => HandleScheduledScanAsync(message, CreatorSourceScanMode.Full, stoppingToken),
                stoppingToken)
        };

        logger.LogInformation("Subscribed to channel discovery consumers on stream {Stream}.", Stream.Value);
        return Task.WhenAll(consumers);
    }

    private Task Consume<TMessage>(
        string consumerName,
        Func<TMessage, Task> handler,
        CancellationToken stoppingToken)
        where TMessage : ScheduledBackgroundRequest
        => consumer.ConsumePullAsync<TMessage>(
            Stream,
            ConsumerName.From(consumerName),
            async context =>
            {
                try
                {
                    await handler(context.Message);
                    await context.AckAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed handling channel discovery request {IdempotencyKey}; nacking", context.Message.IdempotencyKey);
                    await context.NackAsync();
                }
            },
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleScheduledScanAsync(
        ScheduledBackgroundRequest request,
        CreatorSourceScanMode scanMode,
        CancellationToken cancellationToken)
    {
        await MarkAttemptAsync(request, cancellationToken);

        var sourcesResponse = await messageBus.RequestAsync<CreatorSourceListEnabledForScanRequestMessage, CreatorSourceOperationResponseMessage>(
            CreatorDiscoverySubjects.ListEnabledSourcesForScan,
            new CreatorSourceListEnabledForScanRequestMessage { ScanMode = scanMode },
            RequestTimeout,
            cancellationToken);

        if (sourcesResponse is not { Success: true })
        {
            throw new InvalidOperationException(sourcesResponse?.ErrorMessage ?? "Creator source list request failed.");
        }

        var sources = sourcesResponse.Items ?? Array.Empty<CreatorSourceDto>();
        foreach (var source in sources)
        {
            await ScanSourceAsync(request, scanMode, source, cancellationToken);
        }

        await MarkSuccessAsync(request, cancellationToken);
        logger.LogInformation(
            "Completed {ScanMode} channel discovery request {IdempotencyKey} across {SourceCount} source(s).",
            scanMode,
            request.IdempotencyKey,
            sources.Count);
    }

    private async Task ScanSourceAsync(
        ScheduledBackgroundRequest request,
        CreatorSourceScanMode scanMode,
        CreatorSourceDto source,
        CancellationToken cancellationToken)
    {
        var options = BuildOptions(scanMode, source);
        var result = await ytDlp.TryGetVideoInfoAsync(source.SourceUrl, cancellationToken, flat: true, overrideOptions: options);
        if (!result.Success || result.Data is not { } container)
        {
            throw new InvalidOperationException($"yt-dlp flat scan failed for creator source {source.Id}: {result.ErrorOutput}");
        }

        var candidates = ExtractCandidates(source, container).ToArray();
        var response = await messageBus.RequestAsync<UpsertDiscoveredMediaBatchRequestMessage, UpsertDiscoveredMediaBatchResponseMessage>(
            CreatorDiscoverySubjects.UpsertDiscoveredMediaBatch,
            new UpsertDiscoveredMediaBatchRequestMessage
            {
                CreatorSourceId = source.Id,
                ScanMode = scanMode,
                ScheduleKey = request.ScheduleKey,
                IdempotencyKey = $"{request.IdempotencyKey}:{source.Id}",
                ScannedAt = clock.GetCurrentInstant(),
                Items = candidates
            },
            RequestTimeout,
            cancellationToken);

        if (response is not { Success: true })
        {
            throw new InvalidOperationException(response?.ErrorMessage ?? $"Discovery upsert failed for creator source {source.Id}.");
        }

        logger.LogInformation(
            "Scanned creator source {SourceId} ({ScanMode}); seen {SeenCount}, new {NewCount}, changed {ChangedCount}.",
            source.Id,
            scanMode,
            response.TotalSeen,
            response.NewCount,
            response.ChangedCount);
    }

    private static YtDlpOptions? BuildOptions(CreatorSourceScanMode scanMode, CreatorSourceDto source)
        => scanMode == CreatorSourceScanMode.Incremental
            ? new YtDlpOptions
            {
                VideoSelection = new YtDlpVideoSelectionOptions
                {
                    PlaylistItems = $"1:{source.IncrementalPageSize}"
                }
            }
            : null;

    private static IEnumerable<DiscoveredMediaCandidate> ExtractCandidates(CreatorSourceDto source, VideoInfo container)
    {
        var entries = container.Entries ?? Array.Empty<VideoInfo>();
        foreach (var entry in entries)
        {
            var externalId = FirstNonBlank(entry.Id, entry.DisplayId);
            if (externalId is null)
            {
                continue;
            }

            var canonicalUrl = ResolveCanonicalUrl(source, entry, externalId);
            if (canonicalUrl is null)
            {
                continue;
            }

            yield return new DiscoveredMediaCandidate
            {
                Platform = source.Platform,
                Extractor = FirstNonBlank(entry.Extractor, entry.ExtractorKey, container.Extractor, container.ExtractorKey, source.Platform) ?? source.Platform,
                ExternalMediaId = externalId,
                CanonicalUrl = canonicalUrl,
                Title = FirstNonBlank(entry.Title, entry.FullTitle),
                DurationSeconds = entry.Duration,
                ThumbnailUrl = BestThumbnailUrl(entry),
                LiveStatus = entry.LiveStatus?.ToString(),
                Availability = entry.Availability?.ToString()
            };
        }
    }

    private static string? ResolveCanonicalUrl(CreatorSourceDto source, VideoInfo entry, string externalId)
    {
        var url = FirstAbsoluteUrl(entry.WebpageUrl, entry.Url);
        if (url is not null)
        {
            return url;
        }

        var extractor = FirstNonBlank(entry.Extractor, entry.ExtractorKey, source.Platform);
        if (extractor?.Contains("youtube", StringComparison.OrdinalIgnoreCase) == true ||
            source.Platform.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://www.youtube.com/watch?v={externalId}";
        }

        return null;
    }

    private static string? BestThumbnailUrl(VideoInfo entry)
        => entry.Thumbnails?
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .OrderByDescending(x => x.Preference ?? 0)
            .ThenByDescending(x => x.Width ?? 0)
            .Select(x => x.Url)
            .FirstOrDefault();

    private static string? FirstAbsoluteUrl(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) &&
            Uri.TryCreate(value, UriKind.Absolute, out _));

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private Task MarkAttemptAsync(ScheduledBackgroundRequest message, CancellationToken cancellationToken)
        => messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
        {
            Key = message.ScheduleKey,
            AttemptedAt = clock.GetCurrentInstant()
        }, cancellationToken: cancellationToken);

    private Task MarkSuccessAsync(ScheduledBackgroundRequest message, CancellationToken cancellationToken)
        => messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
        {
            Key = message.ScheduleKey,
            SucceededAt = clock.GetCurrentInstant()
        }, cancellationToken: cancellationToken);
}
