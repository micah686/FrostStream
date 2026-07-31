using NodaTime;
using Shared.Database;

namespace Shared.Messaging;

public sealed record CreatorMonitorDto
{
    public required long Id { get; init; }
    public required string Platform { get; init; }
    public required CreatorSourceType SourceType { get; init; }
    public required string SourceUrl { get; init; }

    /// <summary>The metadata.accounts row this source belongs to. Null until channel metadata
    /// resolves it (and for sources whose account cannot be derived).</summary>
    public long? AccountId { get; init; }

    public required bool ScanEnabled { get; init; }
    public required int IncrementalPageSize { get; init; }
    public required int ConsecutiveKnownThreshold { get; init; }
    public required int FullRescanIntervalDays { get; init; }
    public required int UpdateCheckIntervalHours { get; init; }
    public required int MetadataRefreshWindow { get; init; }
    public Instant? LastSuccessfulScanAt { get; init; }
    public Instant? LastFullScanAt { get; init; }
    public string? LastSeenHighWatermark { get; init; }
    public int? NextFullScanStartIndex { get; init; }
    public required Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
    public string? AvatarUrl { get; init; }
    public string? AvatarContentHash { get; init; }
    public string? BannerUrl { get; init; }
    public string? BannerContentHash { get; init; }
    public Instant? AssetsLastRefreshedAt { get; init; }
    public Instant? AssetsLastAttemptAt { get; init; }
    public int AssetsAttemptCount { get; init; }
    public string? AssetsLastError { get; init; }
}

public sealed record CreatorMonitorCreateRequestMessage
{
    public required string SourceUrl { get; init; }
    public bool ScanEnabled { get; init; } = true;
    public int IncrementalPageSize { get; init; } = 50;
    public int ConsecutiveKnownThreshold { get; init; } = 25;
    public int FullRescanIntervalDays { get; init; } = 30;
    public int UpdateCheckIntervalHours { get; init; } = 6;
    public int MetadataRefreshWindow { get; init; } = 25;
}

public sealed record CreatorMonitorCreateOrReuseRequestMessage
{
    public required string SourceUrl { get; init; }
    public bool ScanEnabled { get; init; } = true;
    public int IncrementalPageSize { get; init; } = 50;
    public int ConsecutiveKnownThreshold { get; init; } = 25;
    public int FullRescanIntervalDays { get; init; } = 30;
    public int UpdateCheckIntervalHours { get; init; } = 6;
    public int MetadataRefreshWindow { get; init; } = 25;
}

public sealed record CreatorMonitorUpdateRequestMessage
{
    public required long Id { get; init; }
    public required string SourceUrl { get; init; }
    public bool ScanEnabled { get; init; } = true;
    public int IncrementalPageSize { get; init; } = 50;
    public int ConsecutiveKnownThreshold { get; init; } = 25;
    public int FullRescanIntervalDays { get; init; } = 30;
    public int UpdateCheckIntervalHours { get; init; } = 6;
    public int MetadataRefreshWindow { get; init; } = 25;
}

public sealed record CreatorMonitorGetRequestMessage
{
    public required long Id { get; init; }
}

public sealed record CreatorMonitorListRequestMessage;

public sealed record CreatorMonitorListEnabledForScanRequestMessage
{
    public required CreatorSourceScanMode ScanMode { get; init; }
}

public sealed record CreatorMonitorDeleteRequestMessage
{
    public required long Id { get; init; }
}

public sealed record CreatorMonitorOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public CreatorMonitorDto? Entity { get; init; }
    public IReadOnlyList<CreatorMonitorDto>? Items { get; init; }
}

public sealed record DiscoveredMediaCandidate
{
    public required string Platform { get; init; }
    public required string Extractor { get; init; }
    public required string ExternalMediaId { get; init; }
    public required string CanonicalUrl { get; init; }
    public string? Title { get; init; }
    public double? DurationSeconds { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? LiveStatus { get; init; }
    public string? Availability { get; init; }
}

public sealed record UpsertDiscoveredMediaBatchRequestMessage
{
    public required long CreatorSourceId { get; init; }
    /// <summary>Shared correlation/group identifier for every download job produced by this scan.</summary>
    public Guid CorrelationId { get; init; }
    public required CreatorSourceScanMode ScanMode { get; init; }
    public required string ScheduleKey { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Instant ScannedAt { get; init; }
    public string? ScanHighWatermarkExternalMediaId { get; init; }
    public int? ScanPageStartIndex { get; init; }
    public int? NextScanPageStartIndex { get; init; }
    public bool ScanPageComplete { get; init; } = true;
    public bool IsScanPageFinalBatch { get; init; } = true;
    public string? StorageKey { get; init; }
    public string? RequestedBy { get; init; }
    public string? ConfigSetKey { get; init; }
    public string? WorkerTag { get; init; }
    public bool EncodeForPlaylist { get; init; }
    public string? CookieSecretPath { get; init; }
    public int Priority { get; init; }
    public bool FetchComments { get; init; }
    public bool QueueAllItems { get; init; }
    public bool ForceDownload { get; init; }
    /// <summary>Persists discovery results without producing download jobs after a group stop.</summary>
    public bool SuppressDownloadEnqueue { get; init; }
    public YtDlpSharpLib.Options.YtDlpOptions? YtDlpOptions { get; init; }
    public required IReadOnlyList<DiscoveredMediaCandidate> Items { get; init; }
}

public sealed record UpsertDiscoveredMediaBatchResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int TotalSeen { get; init; }
    public int NewCount { get; init; }
    public int ChangedCount { get; init; }
    public IReadOnlyList<DiscoveredMediaCandidate>? EnqueuedItems { get; init; }
}

public sealed record UpdateCreatorMonitorAssetsRequestMessage
{
    public required long SourceId { get; init; }

    // Account identity (derived from the channel's yt-dlp metadata) used to bridge the durable
    // avatar/banner blobs into metadata.accounts — the authoritative table consumers/rescan read.
    public string? Platform { get; init; }
    public string? AccountHandle { get; init; }
    public string? AccountName { get; init; }
    public string? AccountUrl { get; init; }

    // The storage backend (FluentStorage storage_key) the avatar/banner blobs were written to.
    public string? StorageKey { get; init; }

    public string? AvatarUrl { get; init; }
    /// <summary>Durable blob path (within <see cref="StorageKey"/>) of the avatar. Persisted to metadata.accounts.</summary>
    public string? AvatarStoragePath { get; init; }
    public string? AvatarContentHash { get; init; }
    public string? BannerUrl { get; init; }
    /// <summary>Durable blob path (within <see cref="StorageKey"/>) of the banner. Persisted to metadata.accounts.</summary>
    public string? BannerStoragePath { get; init; }
    public string? BannerContentHash { get; init; }
    public Instant? RefreshedAt { get; init; }
    public Instant? AttemptedAt { get; init; }
    public int? AttemptCount { get; init; }
    public string? LastError { get; init; }
    public bool ClearError { get; init; }
}

public sealed record UpdateCreatorMonitorAssetsResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public CreatorMonitorDto? Entity { get; init; }
}
