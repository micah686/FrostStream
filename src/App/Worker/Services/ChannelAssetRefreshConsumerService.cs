using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;

namespace Worker.Services;

public sealed class ChannelAssetRefreshConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IYtDlpClient ytDlp,
    PotOptionsApplier potOptionsApplier,
    AssetCacheWriter assetCacheWriter,
    IOptions<AssetCacheOptions> assetCacheOptions,
    IClock clock,
    ILogger<ChannelAssetRefreshConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly AssetCacheOptions _options = assetCacheOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Subscribing to channel asset refresh consumer on stream {Stream}.", Stream.Value);

        await consumer.ConsumePullAsync<ChannelAssetRefreshRequested>(
            Stream,
            ConsumerName.From(BackgroundJobsTopology.WorkerChannelAssetRefreshConsumer),
            async context =>
            {
                try
                {
                    await HandleAsync(context.Message, stoppingToken);
                    await context.AckAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed handling channel asset refresh {IdempotencyKey}; nacking", context.Message.IdempotencyKey);
                    await context.NackAsync();
                }
            },
            options: null,
            cancellationToken: stoppingToken);
    }

    private async Task HandleAsync(ChannelAssetRefreshRequested request, CancellationToken cancellationToken)
    {
        var sources = await ResolveSourcesAsync(request, cancellationToken);
        if (sources.Count == 0)
        {
            logger.LogInformation("Channel asset refresh {IdempotencyKey} produced no sources.", request.IdempotencyKey);
            return;
        }

        var now = clock.GetCurrentInstant();
        var freshnessThreshold = now.Minus(Duration.FromTimeSpan(_options.FreshnessWindow));

        foreach (var source in sources)
        {
            if (!request.Force &&
                source.AssetsLastRefreshedAt is { } last &&
                last >= freshnessThreshold)
            {
                logger.LogDebug(
                    "Skipping asset refresh for source {SourceId}; last refreshed {LastRefreshed} is within freshness window.",
                    source.Id,
                    last);
                continue;
            }

            await RefreshSourceAsync(source, cancellationToken);
        }

        logger.LogInformation(
            "Completed channel asset refresh {IdempotencyKey} across {Count} source(s).",
            request.IdempotencyKey,
            sources.Count);
    }

    private async Task<IReadOnlyList<CreatorSourceDto>> ResolveSourcesAsync(
        ChannelAssetRefreshRequested request,
        CancellationToken cancellationToken)
    {
        if (request.TargetSourceId is { } id)
        {
            var response = await messageBus.RequestAsync<CreatorSourceGetRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.GetSource,
                new CreatorSourceGetRequestMessage { Id = id },
                RequestTimeout,
                cancellationToken);

            if (response is not { Success: true } || response.Entity is null)
            {
                throw new InvalidOperationException(response?.ErrorMessage ?? $"Creator source '{id}' was not found.");
            }

            return [response.Entity];
        }

        var listResponse = await messageBus.RequestAsync<CreatorSourceListEnabledForScanRequestMessage, CreatorSourceOperationResponseMessage>(
            CreatorDiscoverySubjects.ListEnabledSourcesForScan,
            new CreatorSourceListEnabledForScanRequestMessage { ScanMode = Shared.Database.CreatorSourceScanMode.Incremental },
            RequestTimeout,
            cancellationToken);

        if (listResponse is not { Success: true })
        {
            throw new InvalidOperationException(listResponse?.ErrorMessage ?? "Creator source list request failed.");
        }

        return listResponse.Items ?? Array.Empty<CreatorSourceDto>();
    }

    private async Task RefreshSourceAsync(CreatorSourceDto source, CancellationToken cancellationToken)
    {
        VideoInfo? container;
        try
        {
            var options = new YtDlpOptions
            {
                VideoSelection = new YtDlpVideoSelectionOptions
                {
                    PlaylistItems = "0"
                }
            };
            var result = await ytDlp.TryGetVideoInfoAsync(source.SourceUrl, cancellationToken, flat: false, overrideOptions: potOptionsApplier.Apply(options));
            if (!result.Success || result.Data is not { } info)
            {
                await PublishFailureAsync(source, $"yt-dlp returned no metadata: {result.ErrorOutput}", cancellationToken);
                return;
            }
            container = info;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Channel asset metadata fetch failed for source {SourceId}.", source.Id);
            await PublishFailureAsync(source, ex.Message, cancellationToken);
            return;
        }

        var identity = DeriveIdentity(container);
        var avatar = SelectThumbnail(container.Thumbnails, AssetKind.Avatar);
        var banner = SelectThumbnail(container.Thumbnails, AssetKind.Banner);

        if (avatar is null && banner is null)
        {
            logger.LogInformation("No avatar/banner thumbnails available for source {SourceId} ({SourceUrl}).", source.Id, source.SourceUrl);
            await PublishSuccessAsync(source, identity, null, null, cancellationToken);
            return;
        }

        AssetDownloadResult? avatarResult = null;
        AssetDownloadResult? bannerResult = null;
        string? failure = null;

        if (avatar is not null)
        {
            try
            {
                avatarResult = await assetCacheWriter.DownloadAndStoreAsync(avatar.Url!, AssetKind.Avatar, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Avatar download failed for source {SourceId}.", source.Id);
                failure = $"avatar: {ex.Message}";
            }
        }

        if (banner is not null)
        {
            try
            {
                bannerResult = await assetCacheWriter.DownloadAndStoreAsync(banner.Url!, AssetKind.Banner, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Banner download failed for source {SourceId}.", source.Id);
                failure = failure is null ? $"banner: {ex.Message}" : $"{failure}; banner: {ex.Message}";
            }
        }

        if (avatarResult is null && bannerResult is null && failure is not null)
        {
            await PublishFailureAsync(source, failure, cancellationToken);
            return;
        }

        await PublishSuccessAsync(
            source,
            identity,
            avatarResult is not null ? new AvatarOrBanner(avatar!.Url!, avatarResult) : null,
            bannerResult is not null ? new AvatarOrBanner(banner!.Url!, bannerResult) : null,
            cancellationToken,
            partialFailure: failure);
    }

    private async Task PublishSuccessAsync(
        CreatorSourceDto source,
        ChannelAccountIdentity identity,
        AvatarOrBanner? avatar,
        AvatarOrBanner? banner,
        CancellationToken cancellationToken,
        string? partialFailure = null)
    {
        var now = clock.GetCurrentInstant();
        var request = new UpdateCreatorSourceAssetsRequestMessage
        {
            SourceId = source.Id,
            Platform = identity.Platform,
            AccountHandle = identity.Handle,
            AccountName = identity.Name,
            AccountUrl = identity.Url,
            StorageKey = avatar?.Result.StorageKey ?? banner?.Result.StorageKey,
            AvatarUrl = avatar?.Url,
            AvatarStoragePath = avatar?.Result.StoragePath,
            AvatarContentHash = avatar?.Result.ContentHash,
            BannerUrl = banner?.Url,
            BannerStoragePath = banner?.Result.StoragePath,
            BannerContentHash = banner?.Result.ContentHash,
            RefreshedAt = partialFailure is null ? now : null,
            AttemptedAt = now,
            AttemptCount = partialFailure is null ? 0 : source.AssetsAttemptCount + 1,
            LastError = partialFailure,
            ClearError = partialFailure is null
        };

        var response = await messageBus.RequestAsync<UpdateCreatorSourceAssetsRequestMessage, UpdateCreatorSourceAssetsResponseMessage>(
            CreatorDiscoverySubjects.UpdateAssets,
            request,
            RequestTimeout,
            cancellationToken);

        if (response is not { Success: true })
        {
            logger.LogWarning(
                "Failed to persist updated assets for source {SourceId}: {Error}",
                source.Id,
                response?.ErrorMessage ?? "no response");
        }
    }

    private async Task PublishFailureAsync(CreatorSourceDto source, string error, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var request = new UpdateCreatorSourceAssetsRequestMessage
        {
            SourceId = source.Id,
            AttemptedAt = now,
            AttemptCount = source.AssetsAttemptCount + 1,
            LastError = Truncate(error, 2048)
        };

        var response = await messageBus.RequestAsync<UpdateCreatorSourceAssetsRequestMessage, UpdateCreatorSourceAssetsResponseMessage>(
            CreatorDiscoverySubjects.UpdateAssets,
            request,
            RequestTimeout,
            cancellationToken);

        if (response is not { Success: true })
        {
            logger.LogWarning(
                "Failed to record asset refresh failure for source {SourceId}: {Error}",
                source.Id,
                response?.ErrorMessage ?? "no response");
        }
    }

    private static ThumbnailInfo? SelectThumbnail(IReadOnlyList<ThumbnailInfo>? thumbnails, AssetKind kind)
    {
        if (thumbnails is null || thumbnails.Count == 0)
        {
            return null;
        }

        var candidates = thumbnails.Where(t => !string.IsNullOrWhiteSpace(t.Url)).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var prefix = kind == AssetKind.Avatar ? "avatar" : "banner";
        var uncroppedId = $"{prefix}_uncropped";

        var idMatches = candidates
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) &&
                t.Id!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (idMatches.Length > 0)
        {
            var uncropped = idMatches.FirstOrDefault(t =>
                string.Equals(t.Id, uncroppedId, StringComparison.OrdinalIgnoreCase));
            if (uncropped is not null)
            {
                return uncropped;
            }

            return idMatches
                .OrderByDescending(t => t.Preference ?? 0)
                .ThenByDescending(t => t.Width ?? 0)
                .First();
        }

        return kind == AssetKind.Avatar
            ? SelectByAspect(candidates, t => t.Width is { } w && t.Height is { } h && h > 0 && (double)w / h is >= 0.9 and <= 1.1)
            : SelectByAspect(candidates, t => t.Width is { } w && t.Height is { } h && h > 0 && (double)w / h >= 3.0);
    }

    private static ThumbnailInfo? SelectByAspect(IEnumerable<ThumbnailInfo> candidates, Func<ThumbnailInfo, bool> predicate)
        => candidates
            .Where(predicate)
            .OrderByDescending(t => t.Preference ?? 0)
            .ThenByDescending(t => t.Width ?? 0)
            .FirstOrDefault();

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Derives the account identity for the channel using the same fields the per-media metadata
    /// mapper uses, so the avatar/banner blobs land on the same <c>metadata.accounts</c> row that
    /// media downloads from this channel populate.
    /// </summary>
    private static ChannelAccountIdentity DeriveIdentity(VideoInfo info)
        => new(
            FirstNonBlank(info.Extractor, info.ExtractorKey),
            FirstNonBlank(info.UploaderId, info.ChannelId, info.Uploader, info.Channel),
            FirstNonBlank(info.Uploader, info.Channel, info.Creator),
            FirstNonBlank(info.UploaderUrl, info.ChannelUrl));

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private sealed record ChannelAccountIdentity(string? Platform, string? Handle, string? Name, string? Url);

    private sealed record AvatarOrBanner(string Url, AssetDownloadResult Result);
}
