using NodaTime;

namespace Shared.Messaging;

/// <summary>
/// NATS Core request/reply subject for the admin-facing, system-wide encoding queue read surface
/// (every stream and audio rendition job regardless of what triggered it). Read-only, answered by
/// DataBridge; no JetStream/topology involved, mirrors <see cref="DownloadQueueSubjects"/>.
/// </summary>
public static class RenditionQueueSubjects
{
    public const string List = "rendition-queue.list";

    /// <summary>Queue group so a single DataBridge instance answers each query.</summary>
    public const string QueueGroup = "databridge-rendition-queue";
}

/// <summary>
/// Request for a paged slice of every stream/audio rendition row across the system, with optional
/// filters. Progress is intentionally excluded — it is delivered live-only over the SSE route.
/// </summary>
public sealed record RenditionQueueListRequest
{
    public RenditionKind? Kind { get; init; }

    /// <summary>One of "Pending", "Processing", "Ready", "Failed".</summary>
    public string? Status { get; init; }

    public string? StorageKey { get; init; }

    /// <summary>Free-text substring match against media title or media guid.</summary>
    public string? Query { get; init; }

    /// <summary>Requested page size. The read model clamps this to a safe range.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Opaque continuation token returned by a previous page; null for the first page.</summary>
    public string? Cursor { get; init; }
}

public sealed record RenditionQueueListResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<RenditionQueueItemDto> Items { get; init; } = [];

    /// <summary>Opaque token to pass as <see cref="RenditionQueueListRequest.Cursor"/> for the next page; null when exhausted.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total number of renditions matching the filters (across all pages).</summary>
    public int TotalCount { get; init; }
}

/// <summary>Snapshot of one stream or audio rendition row for the system-wide encoding queue surface.</summary>
public sealed record RenditionQueueItemDto
{
    public required Guid RenditionId { get; init; }
    public required RenditionKind Kind { get; init; }
    public required Guid MediaGuid { get; init; }
    public required string Title { get; init; }
    public required int SourceVersion { get; init; }

    /// <summary>One of "Pending", "Processing", "Ready", "Failed".</summary>
    public required string Status { get; init; }

    public required string StorageKey { get; init; }
    public string? StoragePath { get; init; }
    public long? SizeBytes { get; init; }
    public int? DurationSeconds { get; init; }
    public string? ErrorMessage { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
}
