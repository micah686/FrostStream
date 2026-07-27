using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Downloads;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class CreatorDiscoveryRepository(DataBridgeDbContext db, IClock clock) : ICreatorDiscoveryRepository
{
    public async Task<CreatorSourceRecord?> GetSourceAsync(long id, CancellationToken cancellationToken = default)
    {
        var source = await db.CreatorSources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return source is null ? null : new CreatorSourceRecord(source, await ReadScanStateAsync(id, cancellationToken));
    }

    public async Task<IReadOnlyList<CreatorSourceRecord>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        var sources = await db.CreatorSources.AsNoTracking()
            .OrderBy(x => x.Platform)
            .ThenBy(x => x.SourceType)
            .ThenBy(x => x.SourceUrl)
            .ToListAsync(cancellationToken);

        return await PairWithScanStateAsync(sources, cancellationToken);
    }

    public async Task<IReadOnlyList<CreatorSourceRecord>> ListEnabledSourcesForScanAsync(
        CreatorSourceScanMode scanMode,
        CancellationToken cancellationToken = default)
    {
        var sources = await db.CreatorSources.AsNoTracking()
            .Where(x => x.ScanEnabled)
            .OrderBy(x => x.Platform)
            .ThenBy(x => x.SourceType)
            .ThenBy(x => x.SourceUrl)
            .ToListAsync(cancellationToken);

        var records = await PairWithScanStateAsync(sources, cancellationToken);
        var now = clock.GetCurrentInstant();

        if (scanMode != CreatorSourceScanMode.Full)
        {
            // Per-source incremental cadence: the global channel-scan-refresh schedule ticks every
            // 30 minutes, and each tick only scans sources due by their UpdateCheckIntervalHours.
            // The half-tick tolerance keeps an interval that matches the tick from drifting (a scan
            // finishing just after a tick would otherwise slip a whole extra tick every cycle).
            var tolerance = Duration.FromMinutes(15);
            return records
                .Where(x => x.ScanState?.LastSuccessfulScanAt is not { } lastScan ||
                    lastScan
                        .Plus(Duration.FromHours(x.Source.UpdateCheckIntervalHours))
                        .Minus(tolerance) <= now)
                .ToList();
        }

        return records
            .Where(x => x.ScanState?.NextFullScanStartIndex is not null ||
                x.ScanState?.LastFullScanAt is not { } lastFullScan ||
                lastFullScan.Plus(Duration.FromDays(x.Source.FullRescanIntervalDays)) <= now)
            .ToList();
    }

    public async Task<CreatorSourceRecord> CreateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default)
    {
        source.SourceUrl = SourceUrlCanonicalizer.Canonicalize(source.SourceUrl);
        db.CreatorSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatorSourceRecord(source, await CreateScanStateAsync(source.Id, cancellationToken));
    }

    public async Task<CreatorSourceRecord> CreateOrReuseSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default)
    {
        // Dedupe is exact string equality on source_url, so both sides must be canonical.
        source.SourceUrl = SourceUrlCanonicalizer.Canonicalize(source.SourceUrl);
        var existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.SourceUrl == source.SourceUrl, cancellationToken);
        if (existing is not null)
        {
            return new CreatorSourceRecord(existing, await ReadScanStateAsync(existing.Id, cancellationToken));
        }

        db.CreatorSources.Add(source);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new CreatorSourceRecord(source, await CreateScanStateAsync(source.Id, cancellationToken));
        }
        catch (DbUpdateException)
        {
            db.Entry(source).State = EntityState.Detached;
            existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.SourceUrl == source.SourceUrl, cancellationToken);
            if (existing is not null)
            {
                return new CreatorSourceRecord(existing, await ReadScanStateAsync(existing.Id, cancellationToken));
            }

            throw;
        }
    }

    public async Task<CreatorSourceRecord?> UpdateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default)
    {
        var existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.Id == source.Id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var scanState = await GetOrCreateScanStateAsync(source.Id, cancellationToken);

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
        existing.UpdateCheckIntervalHours = source.UpdateCheckIntervalHours;
        existing.MetadataRefreshWindow = source.MetadataRefreshWindow;
        existing.ProviderQueryLimitsJson = source.ProviderQueryLimitsJson;
        if (sourceChanged)
        {
            // Repointing a source at a different platform/type/URL invalidates both the scan cursor
            // and the account it was linked to; the next asset refresh re-resolves the account.
            existing.AccountId = null;
            scanState.LastFullScanAt = null;
            scanState.NextFullScanStartIndex = null;
        }
        existing.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return new CreatorSourceRecord(existing, scanState);
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

        // Ignore keywords only apply to user-initiated full downloads, never to background
        // (incremental) monitoring scans, which carry no requesting user.
        var ignoreKeywords = await LoadIgnoreKeywordsAsync(request, cancellationToken);

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
                var ignoreMatch = IgnoreKeywordMatcher.FirstMatch(candidate.Title, ignoreKeywords);
                var suppressed = request.SuppressDownloadEnqueue;
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
                    DiscoveryStatus = ignoreMatch is not null ? MediaDiscoveryStatus.Ignored
                        : suppressed ? MediaDiscoveryStatus.Discovered : MediaDiscoveryStatus.Queued,
                    IgnoredKeyword = ignoreMatch?.Pattern,
                    MetadataStatus = suppressed ? MediaMetadataStatus.PendingEnrichment : MediaMetadataStatus.RefreshRequested,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    LastChangedAt = now,
                    LastEnqueuedAt = suppressed ? null : now
                });
                newCount++;
                if (ignoreMatch is null && !suppressed)
                {
                    enqueued.Add(candidate);
                }

                continue;
            }

            // A video suppressed by an ignore keyword stays ignored across later scans (manual or
            // background) until it is explicitly force-queued — never re-enqueue it here.
            if (existing.DiscoveryStatus == MediaDiscoveryStatus.Ignored)
            {
                existing.LastSeenAt = now;
                existing.MissedFullScanCount = 0;
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
                changedCount++;
            }

            if (!request.SuppressDownloadEnqueue && (changed || request.QueueAllItems))
            {
                existing.LastEnqueuedAt = now;
                existing.MetadataStatus = MediaMetadataStatus.RefreshRequested;
                enqueued.Add(candidate);
            }
        }

        var scanState = await GetOrCreateScanStateAsync(source.Id, cancellationToken);
        scanState.LastSuccessfulScanAt = now;
        source.LastUpdated = now;
        scanState.LastSeenHighWatermark = request.ScanHighWatermarkExternalMediaId
            ?? request.Items.FirstOrDefault()?.ExternalMediaId
            ?? scanState.LastSeenHighWatermark;
        if (request.ScanMode == CreatorSourceScanMode.Full && request.IsScanPageFinalBatch)
        {
            if (request.ScanPageComplete)
            {
                scanState.LastFullScanAt = now;
                scanState.NextFullScanStartIndex = null;
            }
            else
            {
                scanState.NextFullScanStartIndex = request.NextScanPageStartIndex;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new DiscoveredMediaUpsertResult(request.Items.Count, newCount, changedCount, enqueued);
    }

    public async Task<IReadOnlyList<DiscoveredMediaEntity>> ListIgnoredMediaAsync(
        long creatorSourceId,
        CancellationToken cancellationToken = default)
        => await db.DiscoveredMedia
            .AsNoTracking()
            .Where(x => x.CreatorSourceId == creatorSourceId && x.DiscoveryStatus == MediaDiscoveryStatus.Ignored)
            .OrderByDescending(x => x.FirstSeenAt)
            .ToListAsync(cancellationToken);

    public async Task<DiscoveredMediaEntity?> RequeueIgnoredMediaAsync(
        long discoveredMediaId,
        CancellationToken cancellationToken = default)
    {
        var entity = await db.DiscoveredMedia.FirstOrDefaultAsync(x => x.Id == discoveredMediaId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var now = clock.GetCurrentInstant();
        entity.DiscoveryStatus = MediaDiscoveryStatus.Queued;
        entity.IgnoredKeyword = null;
        entity.MetadataStatus = MediaMetadataStatus.RefreshRequested;
        entity.LastChangedAt = now;
        entity.LastEnqueuedAt = now;
        entity.LastUpdated = now;
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private async Task<IReadOnlyList<IgnoreKeyword>> LoadIgnoreKeywordsAsync(
        UpsertDiscoveredMediaBatchRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.ScanMode != CreatorSourceScanMode.Full
            || string.IsNullOrWhiteSpace(request.RequestedBy)
            || string.IsNullOrWhiteSpace(request.ConfigSetKey))
        {
            return [];
        }

        var json = await db.DownloadConfigSets
            .AsNoTracking()
            .Where(x => x.OwnerSubject == request.RequestedBy && x.Key == request.ConfigSetKey)
            .Select(x => x.IgnoreKeywordsJson)
            .FirstOrDefaultAsync(cancellationToken);

        return IgnoreKeywordMatcher.Deserialize(json);
    }

    public async Task<CreatorSourceRecord?> UpdateAssetsAsync(
        UpdateCreatorMonitorAssetsRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.Id == request.SourceId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var now = clock.GetCurrentInstant();
        var scanState = await GetOrCreateScanStateAsync(existing.Id, cancellationToken);

        // The durable avatar/banner blob path lives in metadata.accounts (the authoritative table).
        // The scan state only records what the last refresh fetched.
        if (!string.IsNullOrWhiteSpace(request.AvatarUrl))
        {
            scanState.AvatarUrl = request.AvatarUrl;
            scanState.AvatarContentHash = request.AvatarContentHash;
        }

        if (!string.IsNullOrWhiteSpace(request.BannerUrl))
        {
            scanState.BannerUrl = request.BannerUrl;
            scanState.BannerContentHash = request.BannerContentHash;
        }

        if (request.RefreshedAt is { } refreshedAt)
        {
            scanState.AssetsLastRefreshedAt = refreshedAt;
        }

        if (request.AttemptedAt is { } attemptedAt)
        {
            scanState.AssetsLastAttemptAt = attemptedAt;
        }

        if (request.AttemptCount is { } attemptCount)
        {
            scanState.AssetsAttemptCount = attemptCount;
        }

        if (request.ClearError)
        {
            scanState.AssetsLastError = null;
        }
        else if (request.LastError is not null)
        {
            scanState.AssetsLastError = request.LastError;
        }

        existing.LastUpdated = now;
        await db.SaveChangesAsync(cancellationToken);
        return new CreatorSourceRecord(existing, scanState);
    }

    public async Task LinkAccountAsync(long creatorSourceId, long accountId, CancellationToken cancellationToken = default)
    {
        var existing = await db.CreatorSources.FirstOrDefaultAsync(x => x.Id == creatorSourceId, cancellationToken);
        if (existing is null || existing.AccountId == accountId)
        {
            return;
        }

        existing.AccountId = accountId;
        existing.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<CreatorScanStateEntity?> ReadScanStateAsync(long creatorSourceId, CancellationToken cancellationToken)
        => db.CreatorScanStates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CreatorSourceId == creatorSourceId, cancellationToken);

    private async Task<IReadOnlyList<CreatorSourceRecord>> PairWithScanStateAsync(
        IReadOnlyList<CreatorSourceEntity> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var ids = sources.Select(x => x.Id).ToArray();
        var states = await db.CreatorScanStates.AsNoTracking()
            .Where(x => ids.Contains(x.CreatorSourceId))
            .ToDictionaryAsync(x => x.CreatorSourceId, cancellationToken);

        return sources
            .Select(x => new CreatorSourceRecord(x, states.GetValueOrDefault(x.Id)))
            .ToList();
    }

    private async Task<CreatorScanStateEntity> CreateScanStateAsync(long creatorSourceId, CancellationToken cancellationToken)
    {
        var state = new CreatorScanStateEntity { CreatorSourceId = creatorSourceId };
        db.CreatorScanStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);
        return state;
    }

    /// <summary>
    /// Returns the tracked scan-state row for a source, adding it when missing. Rows are created
    /// alongside the source, so the fallback only covers sources that predate this table.
    /// </summary>
    private async Task<CreatorScanStateEntity> GetOrCreateScanStateAsync(long creatorSourceId, CancellationToken cancellationToken)
    {
        var state = await db.CreatorScanStates.FirstOrDefaultAsync(x => x.CreatorSourceId == creatorSourceId, cancellationToken);
        if (state is not null)
        {
            return state;
        }

        state = new CreatorScanStateEntity { CreatorSourceId = creatorSourceId };
        db.CreatorScanStates.Add(state);
        return state;
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
