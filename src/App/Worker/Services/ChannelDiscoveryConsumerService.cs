using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using YtDlpSharpLib;
using static Shared.Metadata.CreatorIdentity;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;

namespace Worker.Services;

public sealed class ChannelDiscoveryConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IYtDlpClient ytDlp,
    PotOptionsApplier potOptionsApplier,
    IClock clock,
    ILogger<ChannelDiscoveryConsumerService> logger) : BackgroundService
{
    internal const int MaxIncrementalScanEntries = 500;
    internal const int MaxFullScanEntriesPerSource = 5_000;
    internal const int DiscoveryUpsertBatchSize = 100;

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

        var sources = await ResolveSourcesAsync(request, scanMode, cancellationToken);
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

    private async Task<IReadOnlyList<CreatorSourceDto>> ResolveSourcesAsync(
        ScheduledBackgroundRequest request,
        CreatorSourceScanMode scanMode,
        CancellationToken cancellationToken)
    {
        var requestedSourceId = request switch
        {
            ChannelMediaListRequested { TargetSourceId: { } id } => (long?)id,
            ChannelUpdateCheckRequested { TargetSourceId: { } id } => id,
            _ => null
        };

        if (requestedSourceId is { } targetSourceId)
        {
            var sourceResponse = await messageBus.RequestAsync<CreatorSourceGetRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.GetSource,
                new CreatorSourceGetRequestMessage { Id = targetSourceId },
                RequestTimeout,
                cancellationToken);

            if (sourceResponse is not { Success: true, Entity: { } source })
            {
                throw new InvalidOperationException(sourceResponse?.ErrorMessage ?? $"Creator source '{targetSourceId}' was not found.");
            }

            return [source];
        }

        var sourcesResponse = await messageBus.RequestAsync<CreatorSourceListEnabledForScanRequestMessage, CreatorSourceOperationResponseMessage>(
            CreatorDiscoverySubjects.ListEnabledSourcesForScan,
            new CreatorSourceListEnabledForScanRequestMessage { ScanMode = scanMode },
            RequestTimeout,
            cancellationToken);

        if (sourcesResponse is not { Success: true })
        {
            throw new InvalidOperationException(sourcesResponse?.ErrorMessage ?? "Creator source list request failed.");
        }

        return sourcesResponse.Items ?? Array.Empty<CreatorSourceDto>();
    }

    private async Task ScanSourceAsync(
        ScheduledBackgroundRequest request,
        CreatorSourceScanMode scanMode,
        CreatorSourceDto source,
        CancellationToken cancellationToken)
    {
        var requestQueryLimits = ChannelProviderQueryLimits(request);
        var options = BuildOptions(scanMode, source, requestQueryLimits);
        var result = await ytDlp.TryGetVideoInfoAsync(source.SourceUrl, cancellationToken, flat: true, overrideOptions: potOptionsApplier.Apply(options));
        if (!result.Success || result.Data is not { } container)
        {
            throw new InvalidOperationException($"yt-dlp flat scan failed for creator source {source.Id}: {result.ErrorOutput}");
        }

        var candidates = ExtractCandidates(source, container)
            .Take(EntryLimit(scanMode, source, requestQueryLimits))
            .ToArray();
        var scanHighWatermark = candidates.FirstOrDefault()?.ExternalMediaId;
        var pageStartIndex = PageStartIndex(scanMode, source);
        var entryLimit = EntryLimit(scanMode, source, requestQueryLimits);
        var scanPageComplete = scanMode != CreatorSourceScanMode.Full || candidates.Length < entryLimit;
        int? nextScanPageStartIndex = scanPageComplete ? null : pageStartIndex + candidates.Length;

        var totalSeen = 0;
        var newCount = 0;
        var changedCount = 0;
        var batchIndex = 0;
        var batches = Chunk(candidates, DiscoveryUpsertBatchSize).ToArray();

        foreach (var batch in batches)
        {
            var response = await messageBus.RequestAsync<UpsertDiscoveredMediaBatchRequestMessage, UpsertDiscoveredMediaBatchResponseMessage>(
                CreatorDiscoverySubjects.UpsertDiscoveredMediaBatch,
                new UpsertDiscoveredMediaBatchRequestMessage
                {
                    CreatorSourceId = source.Id,
                    ScanMode = scanMode,
                    ScheduleKey = request.ScheduleKey,
                    IdempotencyKey = $"{request.IdempotencyKey}:{source.Id}:batch-{batchIndex}",
                    ScannedAt = clock.GetCurrentInstant(),
                    ScanHighWatermarkExternalMediaId = scanHighWatermark,
                    ScanPageStartIndex = pageStartIndex,
                    NextScanPageStartIndex = nextScanPageStartIndex,
                    ScanPageComplete = scanPageComplete,
                    IsScanPageFinalBatch = batchIndex == batches.Length - 1,
                    StorageKey = ChannelStorageKey(request),
                    RequestedBy = ChannelRequestedBy(request),
                    ConfigSetKey = ChannelConfigSetKey(request),
                    EncodeForPlaylist = ChannelEncodeForPlaylist(request),
                    AudioFormat = ChannelAudioFormat(request),
                    CookieSecretPath = ChannelCookieSecretPath(request),
                    Priority = ChannelPriority(request),
                    FetchComments = ChannelFetchComments(request),
                    YtDlpOptions = ChannelYtDlpOptions(request),
                    Items = batch
                },
                RequestTimeout,
                cancellationToken);

            if (response is not { Success: true })
            {
                throw new InvalidOperationException(response?.ErrorMessage ?? $"Discovery upsert failed for creator source {source.Id}.");
            }

            totalSeen += response.TotalSeen;
            newCount += response.NewCount;
            changedCount += response.ChangedCount;
            batchIndex++;
        }

        if (batchIndex == 0)
        {
            var response = await messageBus.RequestAsync<UpsertDiscoveredMediaBatchRequestMessage, UpsertDiscoveredMediaBatchResponseMessage>(
                CreatorDiscoverySubjects.UpsertDiscoveredMediaBatch,
                new UpsertDiscoveredMediaBatchRequestMessage
                {
                    CreatorSourceId = source.Id,
                    ScanMode = scanMode,
                    ScheduleKey = request.ScheduleKey,
                    IdempotencyKey = $"{request.IdempotencyKey}:{source.Id}:batch-0",
                    ScannedAt = clock.GetCurrentInstant(),
                    ScanHighWatermarkExternalMediaId = scanHighWatermark,
                    ScanPageStartIndex = pageStartIndex,
                    NextScanPageStartIndex = nextScanPageStartIndex,
                    ScanPageComplete = scanPageComplete,
                    IsScanPageFinalBatch = true,
                    StorageKey = ChannelStorageKey(request),
                    RequestedBy = ChannelRequestedBy(request),
                    ConfigSetKey = ChannelConfigSetKey(request),
                    EncodeForPlaylist = ChannelEncodeForPlaylist(request),
                    AudioFormat = ChannelAudioFormat(request),
                    CookieSecretPath = ChannelCookieSecretPath(request),
                    Priority = ChannelPriority(request),
                    FetchComments = ChannelFetchComments(request),
                    YtDlpOptions = ChannelYtDlpOptions(request),
                    Items = []
                },
                RequestTimeout,
                cancellationToken);

            if (response is not { Success: true })
            {
                throw new InvalidOperationException(response?.ErrorMessage ?? $"Discovery upsert failed for creator source {source.Id}.");
            }
        }

        if (!scanPageComplete)
        {
            logger.LogWarning(
                "Creator source {SourceId} ({ScanMode}) scanned page {PageStart}-{PageEnd} and will resume at index {NextStart}.",
                source.Id,
                scanMode,
                pageStartIndex,
                pageStartIndex + candidates.Length - 1,
                nextScanPageStartIndex);
        }

        logger.LogInformation(
            "Scanned creator source {SourceId} ({ScanMode}); seen {SeenCount}, new {NewCount}, changed {ChangedCount}, batches {BatchCount}.",
            source.Id,
            scanMode,
            totalSeen,
            newCount,
            changedCount,
            Math.Max(batchIndex, 1));
    }

    internal static YtDlpOptions BuildOptions(
        CreatorSourceScanMode scanMode,
        CreatorSourceDto source,
        CreatorSourceProviderQueryLimits? requestQueryLimits = null)
        => new()
        {
            VideoSelection = new YtDlpVideoSelectionOptions
            {
                PlaylistItems = $"{PageStartIndex(scanMode, source)}:{PageEndIndex(scanMode, source, requestQueryLimits)}"
            }
        };

    internal static int EntryLimit(
        CreatorSourceScanMode scanMode,
        CreatorSourceDto source,
        CreatorSourceProviderQueryLimits? requestQueryLimits = null)
    {
        var providerLimit = requestQueryLimits?.GetLimit(source.Platform, source.SourceType)
            ?? source.ProviderQueryLimits?.GetLimit(source.Platform, source.SourceType);
        if (providerLimit is not null)
        {
            return Math.Clamp(providerLimit.Value, 1, ModeMaxEntries(scanMode));
        }

        return scanMode == CreatorSourceScanMode.Incremental
            ? Math.Clamp(source.IncrementalPageSize, 1, MaxIncrementalScanEntries)
            : MaxFullScanEntriesPerSource;
    }

    internal static int PageStartIndex(CreatorSourceScanMode scanMode, CreatorSourceDto source)
        => scanMode == CreatorSourceScanMode.Full
            ? Math.Max(1, source.NextFullScanStartIndex ?? 1)
            : 1;

    private static int PageEndIndex(
        CreatorSourceScanMode scanMode,
        CreatorSourceDto source,
        CreatorSourceProviderQueryLimits? requestQueryLimits)
        => PageStartIndex(scanMode, source) + EntryLimit(scanMode, source, requestQueryLimits) - 1;

    private static int ModeMaxEntries(CreatorSourceScanMode scanMode)
        => scanMode == CreatorSourceScanMode.Incremental
            ? MaxIncrementalScanEntries
            : MaxFullScanEntriesPerSource;

    private static string? ChannelStorageKey(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest && !string.IsNullOrWhiteSpace(channelRequest.StorageKey)
            ? channelRequest.StorageKey
            : null;

    private static string? ChannelRequestedBy(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest && !string.IsNullOrWhiteSpace(channelRequest.RequestedBy)
            ? channelRequest.RequestedBy
            : null;

    private static string? ChannelConfigSetKey(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest ? channelRequest.ConfigSetKey : null;

    private static bool ChannelEncodeForPlaylist(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest && channelRequest.EncodeForPlaylist;

    private static AudioRenditionFormat ChannelAudioFormat(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest ? channelRequest.AudioFormat : AudioRenditionFormat.Aac;

    private static string? ChannelCookieSecretPath(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest ? channelRequest.CookieSecretPath : null;

    private static int ChannelPriority(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest ? channelRequest.Priority : 0;

    private static bool ChannelFetchComments(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest && channelRequest.FetchComments;

    private static YtDlpOptions? ChannelYtDlpOptions(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest ? channelRequest.YtDlpOptions : null;

    private static CreatorSourceProviderQueryLimits? ChannelProviderQueryLimits(ScheduledBackgroundRequest request)
        => request is ChannelMediaListRequested channelRequest ? channelRequest.ProviderQueryLimits : null;

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

    private static IEnumerable<IReadOnlyList<DiscoveredMediaCandidate>> Chunk(
        IReadOnlyList<DiscoveredMediaCandidate> candidates,
        int size)
    {
        for (var offset = 0; offset < candidates.Count; offset += size)
        {
            yield return candidates
                .Skip(offset)
                .Take(size)
                .ToArray();
        }
    }

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
