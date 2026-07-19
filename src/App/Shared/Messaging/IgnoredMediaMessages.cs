using NodaTime;
using YtDlpSharpLib.Options;

namespace Shared.Messaging;

/// <summary>A discovered-media row that was suppressed by a config-set ignore keyword.</summary>
public sealed record IgnoredMediaDto
{
    public required long Id { get; init; }
    public required long CreatorSourceId { get; init; }
    public string? Title { get; init; }
    public required string CanonicalUrl { get; init; }
    public string? IgnoredKeyword { get; init; }
    public required Instant FirstSeenAt { get; init; }
    public required Instant LastSeenAt { get; init; }
}

public sealed record ListIgnoredMediaRequestMessage
{
    public required long CreatorSourceId { get; init; }
}

public sealed record ListIgnoredMediaResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<IgnoredMediaDto>? Items { get; init; }
}

/// <summary>
/// Force-queues a previously ignored discovered-media row: clears its ignored state and publishes a
/// <c>DownloadRequested</c> with <c>ForceDownload = true</c> using the supplied resolved config.
/// </summary>
public sealed record ForceQueueDiscoveredMediaRequestMessage
{
    public required long DiscoveredMediaId { get; init; }
    public required string RequestedBy { get; init; }
    public required string StorageKey { get; init; }
    public string? CookieSecretPath { get; init; }
    public YtDlpOptions? YtDlpOptions { get; init; }
    public bool EncodeForPlaylist { get; init; }
    public int Priority { get; init; }
    public bool FetchComments { get; init; }
}

public sealed record ForceQueueOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid? JobId { get; init; }
}

/// <summary>
/// Force-queues a playlist entry that was suppressed by an ignore keyword: clears the ignored job
/// state and re-publishes a <c>DownloadRequested</c> (force enabled) using the playlist's snapshotted
/// download config.
/// </summary>
public sealed record PlaylistItemForceQueueRequestMessage
{
    public required Guid PlaylistId { get; init; }
    public required Guid JobId { get; init; }
    public string? RequestedBy { get; init; }
}
