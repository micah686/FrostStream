using NodaTime;
using Shared.Database;

namespace Shared.Messaging;

public abstract record ScheduledBackgroundRequest
{
    public required string ScheduleKey { get; init; }
    public required string TaskType { get; init; }
    public required Instant DueWindowUtc { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Instant OccurredAt { get; init; }
}

/// <summary>
/// Sweeps archived live streams for <c>live_chat.json</c> sidecars that have not been ingested
/// into ClickHouse and queues an ingest for each. This is how enabling live chat replay on an
/// existing library hydrates its history — the sidecars are archived regardless of the flag.
/// </summary>
public sealed record LiveChatBackfillRequested : ScheduledBackgroundRequest
{
    /// <summary>When set, backfill only this media item instead of sweeping the library.</summary>
    public Guid? TargetMediaGuid { get; init; }

    /// <summary>Re-ingest media that already have a chat marker row.</summary>
    public bool Force { get; init; }
}

public sealed record ChannelScanRefreshRequested : ScheduledBackgroundRequest
{
    /// <summary>When set, scan only this creator source (manual "scan now"); scheduled sweeps leave it null.</summary>
    public long? TargetSourceId { get; init; }
}

public sealed record ChannelAssetRefreshRequested : ScheduledBackgroundRequest
{
    public long? TargetSourceId { get; init; }
    /// <summary>When set, refresh assets for this metadata account directly (channel-page manual
    /// refresh); the account's stored URL is used instead of a creator source.</summary>
    public long? TargetAccountId { get; init; }
    public bool Force { get; init; }
    /// <summary>Fetch channel metadata and resolve its account without downloading avatar/banner assets.</summary>
    public bool MetadataOnly { get; init; }
}

public sealed record ChannelScanFullRequested : ScheduledBackgroundRequest
{
    /// <summary>Set for a Cleipnir-supervised V2 channel expansion.</summary>
    public Guid? GroupId { get; init; }
    public Guid? ExpansionDispatchId { get; init; }
    public int ExpansionAttempt { get; init; } = 1;
    public long? TargetSourceId { get; init; }
    /// <summary>
    /// Shared identifier for every per-video job created by this channel request. Manual
    /// requests populate it at the API boundary; scheduled sweeps derive one per source.
    /// </summary>
    public Guid? CorrelationId { get; init; }
    /// <summary>Queue every discovered item, including unchanged items already known to the monitor.</summary>
    public bool QueueAllItems { get; init; }
    /// <summary>Bypass the normal already-downloaded check for every per-video job.</summary>
    public bool ForceDownload { get; init; }
    public string? StorageKey { get; init; }
    public string? RequestedBy { get; init; }
    public string? ConfigSetKey { get; init; }
    public string? WorkerTag { get; init; }
    public bool EncodeForPlaylist { get; init; }
    public string? CookieSecretPath { get; init; }
    public int Priority { get; init; }
    public bool FetchComments { get; init; }
    public YtDlpSharpLib.Options.YtDlpOptions? YtDlpOptions { get; init; }
}

public sealed record DatabaseStaleMediaCleanupRequested : ScheduledBackgroundRequest;

/// <summary>
/// Purges the download-job history of work that has genuinely finished: the job rows, their runs,
/// stage attempts, artifacts, leases, warnings, event history and progress log, plus the group rows
/// they belonged to and the Cleipnir flow instances that drove them.
/// </summary>
public sealed record DownloadHistoryCleanupRequested : ScheduledBackgroundRequest
{
    /// <summary>Purge only work that finished longer ago than this; 30 days when null.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>
    /// Also purge Failed/Stopped jobs and the groups that hold them. Those jobs can no longer be
    /// restarted afterwards, because Start rebuilds the original request from their event history.
    /// </summary>
    public bool IncludeFailed { get; init; }
}

/// <summary>
/// Purges terminal local-media import sessions and the durable <see cref="LocalImportItemFlow"/>
/// instances that drove them.
/// </summary>
public sealed record ImportSessionCleanupRequested : ScheduledBackgroundRequest
{
    /// <summary>Purge only sessions that completed longer ago than this; 30 days when null.</summary>
    public int? RetentionDays { get; init; }
}

public sealed record DatabaseMaintenanceRequested : ScheduledBackgroundRequest;

public sealed record DatabaseMaintenanceReindexRequested : ScheduledBackgroundRequest;

public sealed record SearchReindexRequested : ScheduledBackgroundRequest;
