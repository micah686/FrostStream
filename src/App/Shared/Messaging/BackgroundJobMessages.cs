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

public sealed record ChannelUpdateCheckRequested : ScheduledBackgroundRequest
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
}

public sealed record ChannelMediaListRequested : ScheduledBackgroundRequest
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
    public bool EncodeForPlaylist { get; init; }
    public string? CookieSecretPath { get; init; }
    public int Priority { get; init; }
    public bool FetchComments { get; init; }
    public YtDlpSharpLib.Options.YtDlpOptions? YtDlpOptions { get; init; }
    public CreatorSourceProviderQueryLimits? ProviderQueryLimits { get; init; }
}

public sealed record StaleDatabaseCleanupRequested : ScheduledBackgroundRequest;

public sealed record WatchedItemAutoDeleteRequested : ScheduledBackgroundRequest;

public sealed record ProcessedMessageCleanupRequested : ScheduledBackgroundRequest;

public sealed record DatabaseMaintenanceRequested : ScheduledBackgroundRequest;

public sealed record SearchReindexRequested : ScheduledBackgroundRequest;

public sealed record FilesystemRescanRequested : ScheduledBackgroundRequest;

public sealed record BackupRequested : ScheduledBackgroundRequest
{
    /// <summary>Optional human-readable archive name. Defaults to a timestamp-keyed name when absent.</summary>
    public string? Name { get; init; }
}
