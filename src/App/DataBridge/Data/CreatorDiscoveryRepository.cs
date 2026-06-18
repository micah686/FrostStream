using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class CreatorDiscoveryRepository(DataBridgeDbContext db, IClock clock) : ICreatorDiscoveryRepository
{
    public Task<CreatorSourceEntity?> GetSourceAsync(long id, CancellationToken cancellationToken = default)
        => db.CreatorSources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<CreatorSourceEntity>> ListSourcesAsync(CancellationToken cancellationToken = default)
        => await db.CreatorSources.AsNoTracking()
            .OrderBy(x => x.Platform)
            .ThenBy(x => x.SourceType)
            .ThenBy(x => x.SourceUrl)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CreatorSourceEntity>> ListEnabledSourcesForScanAsync(
        CreatorSourceScanMode scanMode,
        CancellationToken cancellationToken = default)
    {
        var sources = await db.CreatorSources.AsNoTracking()
            .Where(x => x.ScanEnabled)
            .OrderBy(x => x.Platform)
            .ThenBy(x => x.SourceType)
            .ThenBy(x => x.SourceUrl)
            .ToListAsync(cancellationToken);

        if (scanMode != CreatorSourceScanMode.Full)
        {
            return sources;
        }

        var now = clock.GetCurrentInstant();
        return sources
            .Where(x => x.NextFullScanStartIndex is not null ||
                x.LastFullScanAt is null ||
                x.LastFullScanAt.Value.Plus(Duration.FromDays(x.FullRescanIntervalDays)) <= now)
            .ToList();
    }

    public async Task<CreatorSourceEntity> CreateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default)
    {
        db.CreatorSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        return source;
    }

    public async Task<CreatorSourceEntity?> UpdateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default)
    {
        var existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.Id == source.Id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var sourceChanged = !string.Equals(existing.SourceUrl, source.SourceUrl, StringComparison.Ordinal)
            || !string.Equals(existing.Platform, source.Platform, StringComparison.Ordinal)
            || existing.SourceType != source.SourceType;

        existing.Platform = source.Platform;
        existing.SourceType = source.SourceType;
        existing.SourceUrl = source.SourceUrl;
        existing.ScanEnabled = source.ScanEnabled;
        existing.IncrementalPageSize = source.IncrementalPageSize;
        existing.ConsecutiveKnownThreshold = source.ConsecutiveKnownThreshold;
        existing.FullRescanIntervalDays = source.FullRescanIntervalDays;
        existing.MetadataRefreshWindow = source.MetadataRefreshWindow;
        if (sourceChanged)
        {
            existing.LastFullScanAt = null;
            existing.NextFullScanStartIndex = null;
        }
        existing.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteSourceAsync(long id, CancellationToken cancellationToken = default)
    {
        var existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        db.CreatorSources.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<DiscoveredMediaUpsertResult> UpsertDiscoveredMediaBatchAsync(
        UpsertDiscoveredMediaBatchRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var source = await db.CreatorSources.FirstOrDefaultAsync(x => x.Id == request.CreatorSourceId, cancellationToken)
            ?? throw new InvalidOperationException($"Creator source '{request.CreatorSourceId}' was not found.");

        var now = request.ScannedAt;
        var newCount = 0;
        var changedCount = 0;
        var enqueued = new List<DiscoveredMediaCandidate>();
        var seenKeys = new HashSet<(string Platform, string Extractor, string ExternalMediaId)>();

        foreach (var candidate in request.Items)
        {
            var platform = NormalizeRequired(candidate.Platform);
            var extractor = NormalizeRequired(candidate.Extractor);
            var externalMediaId = NormalizeRequired(candidate.ExternalMediaId);
            var key = (platform, extractor, externalMediaId);
            if (!seenKeys.Add(key))
            {
                continue;
            }

            var existing = await db.DiscoveredMedia.FirstOrDefaultAsync(
                x => x.Platform == platform && x.Extractor == extractor && x.ExternalMediaId == externalMediaId,
                cancellationToken);

            if (existing is null)
            {
                db.DiscoveredMedia.Add(new DiscoveredMediaEntity
                {
                    CreatorSourceId = source.Id,
                    Platform = platform,
                    Extractor = extractor,
                    ExternalMediaId = externalMediaId,
                    CanonicalUrl = NormalizeRequired(candidate.CanonicalUrl),
                    Title = NormalizeOptional(candidate.Title),
                    DurationSeconds = candidate.DurationSeconds,
                    ThumbnailUrl = NormalizeOptional(candidate.ThumbnailUrl),
                    LiveStatus = NormalizeOptional(candidate.LiveStatus),
                    Availability = NormalizeOptional(candidate.Availability),
                    DiscoveryStatus = MediaDiscoveryStatus.Queued,
                    MetadataStatus = MediaMetadataStatus.RefreshRequested,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    LastChangedAt = now,
                    LastEnqueuedAt = now
                });
                newCount++;
                enqueued.Add(candidate);
                continue;
            }

            var changed = HasMeaningfulChange(existing, candidate);
            existing.CreatorSourceId = source.Id;
            existing.LastSeenAt = now;
            existing.MissedFullScanCount = 0;
            existing.LastUpdated = now;
            existing.CanonicalUrl = NormalizeRequired(candidate.CanonicalUrl);
            existing.Title = NormalizeOptional(candidate.Title);
            existing.DurationSeconds = candidate.DurationSeconds;
            existing.ThumbnailUrl = NormalizeOptional(candidate.ThumbnailUrl);
            existing.LiveStatus = NormalizeOptional(candidate.LiveStatus);
            existing.Availability = NormalizeOptional(candidate.Availability);

            if (changed)
            {
                existing.LastChangedAt = now;
                existing.LastEnqueuedAt = now;
                existing.MetadataStatus = MediaMetadataStatus.RefreshRequested;
                changedCount++;
                enqueued.Add(candidate);
            }
        }

        source.LastSuccessfulScanAt = now;
        source.LastUpdated = now;
        source.LastSeenHighWatermark = request.ScanHighWatermarkExternalMediaId
            ?? request.Items.FirstOrDefault()?.ExternalMediaId
            ?? source.LastSeenHighWatermark;
        if (request.ScanMode == CreatorSourceScanMode.Full && request.IsScanPageFinalBatch)
        {
            if (request.ScanPageComplete)
            {
                source.LastFullScanAt = now;
                source.NextFullScanStartIndex = null;
            }
            else
            {
                source.NextFullScanStartIndex = request.NextScanPageStartIndex;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new DiscoveredMediaUpsertResult(request.Items.Count, newCount, changedCount, enqueued);
    }

    public async Task<CreatorSourceEntity?> UpdateAssetsAsync(
        UpdateCreatorSourceAssetsRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.Id == request.SourceId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var now = clock.GetCurrentInstant();

        if (!string.IsNullOrWhiteSpace(request.AvatarUrl))
        {
            existing.AvatarUrl = request.AvatarUrl;
            existing.AvatarCachePath = request.AvatarCachePath;
            existing.AvatarContentHash = request.AvatarContentHash;
        }

        if (!string.IsNullOrWhiteSpace(request.BannerUrl))
        {
            existing.BannerUrl = request.BannerUrl;
            existing.BannerCachePath = request.BannerCachePath;
            existing.BannerContentHash = request.BannerContentHash;
        }

        if (request.RefreshedAt is { } refreshedAt)
        {
            existing.AssetsLastRefreshedAt = refreshedAt;
        }

        if (request.AttemptedAt is { } attemptedAt)
        {
            existing.AssetsLastAttemptAt = attemptedAt;
        }

        if (request.AttemptCount is { } attemptCount)
        {
            existing.AssetsAttemptCount = attemptCount;
        }

        if (request.ClearError)
        {
            existing.AssetsLastError = null;
        }
        else if (request.LastError is not null)
        {
            existing.AssetsLastError = request.LastError;
        }

        existing.LastUpdated = now;
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private static bool HasMeaningfulChange(DiscoveredMediaEntity existing, DiscoveredMediaCandidate candidate)
        => !string.Equals(existing.Title, NormalizeOptional(candidate.Title), StringComparison.Ordinal) ||
           existing.DurationSeconds != candidate.DurationSeconds ||
           !string.Equals(existing.ThumbnailUrl, NormalizeOptional(candidate.ThumbnailUrl), StringComparison.Ordinal) ||
           !string.Equals(existing.LiveStatus, NormalizeOptional(candidate.LiveStatus), StringComparison.Ordinal) ||
           !string.Equals(existing.Availability, NormalizeOptional(candidate.Availability), StringComparison.Ordinal);

    private static string NormalizeRequired(string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Required discovery value was blank.") : value.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
