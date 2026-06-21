using NodaTime;

namespace Shared.Database;

public enum CreatorSourceType
{
    Videos = 0,
    Shorts = 1,
    Streams = 2,
    Playlist = 3,
    Clips = 4,
    Vods = 5
}

public enum CreatorSourceScanMode
{
    Incremental = 0,
    Full = 1
}

public enum MediaDiscoveryStatus
{
    Discovered = 0,
    Queued = 1,
    Ignored = 2,
    PossiblyUnavailable = 3,
    Unavailable = 4,
    RemovedFromSource = 5
}

public enum MediaMetadataStatus
{
    PendingEnrichment = 0,
    RefreshRequested = 1,
    Enriched = 2,
    Failed = 3
}

public sealed class CreatorSourceEntity
{
    public long Id { get; set; }

    public required string Platform { get; set; }

    public CreatorSourceType SourceType { get; set; }

    public required string SourceUrl { get; set; }

    public bool ScanEnabled { get; set; } = true;

    public int IncrementalPageSize { get; set; } = 50;

    public int ConsecutiveKnownThreshold { get; set; } = 25;

    public int FullRescanIntervalDays { get; set; } = 30;

    public int MetadataRefreshWindow { get; set; } = 25;

    public Instant? LastSuccessfulScanAt { get; set; }

    public Instant? LastFullScanAt { get; set; }

    public string? LastSeenHighWatermark { get; set; }

    public int? NextFullScanStartIndex { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }

    public string? AvatarUrl { get; set; }

    public string? AvatarContentHash { get; set; }

    public string? BannerUrl { get; set; }

    public string? BannerContentHash { get; set; }

    public Instant? AssetsLastRefreshedAt { get; set; }

    public Instant? AssetsLastAttemptAt { get; set; }

    public int AssetsAttemptCount { get; set; }

    public string? AssetsLastError { get; set; }
}

public sealed class DiscoveredMediaEntity
{
    public long Id { get; set; }

    public long CreatorSourceId { get; set; }

    public required string Platform { get; set; }

    public required string Extractor { get; set; }

    public required string ExternalMediaId { get; set; }

    public required string CanonicalUrl { get; set; }

    public string? Title { get; set; }

    public double? DurationSeconds { get; set; }

    public string? ThumbnailUrl { get; set; }

    public string? LiveStatus { get; set; }

    public string? Availability { get; set; }

    public MediaDiscoveryStatus DiscoveryStatus { get; set; } = MediaDiscoveryStatus.Discovered;

    public MediaMetadataStatus MetadataStatus { get; set; } = MediaMetadataStatus.PendingEnrichment;

    public Instant FirstSeenAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant LastSeenAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public int MissedFullScanCount { get; set; }

    public Instant? LastChangedAt { get; set; }

    public Instant? LastEnqueuedAt { get; set; }

    public Instant? LastUpdated { get; set; }
}
