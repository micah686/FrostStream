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

public sealed record ChannelUpdateCheckRequested : ScheduledBackgroundRequest;

public sealed record ChannelAssetRefreshRequested : ScheduledBackgroundRequest
{
    public long? TargetSourceId { get; init; }
    public bool Force { get; init; }
}

public sealed record ChannelMediaListRequested : ScheduledBackgroundRequest
{
    public long? TargetSourceId { get; init; }
    public string? StorageKey { get; init; }
    public string? RequestedBy { get; init; }
    public string? ConfigSetKey { get; init; }
    public bool EncodeForPlaylist { get; init; }
    public AudioRenditionFormat AudioFormat { get; init; } = AudioRenditionFormat.Aac;
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
