using NodaTime;

namespace Shared.Messaging;

/// <summary>
/// NATS Core request/reply subjects for the admin-facing download queue read surface
/// (full download-job history with filters, per-job detail, and per-job event timeline).
/// These are read-only queries answered by DataBridge; no JetStream/topology is involved.
/// </summary>
public static class DownloadQueueSubjects
{
    public const string List = "download-queue.list";
    public const string Get = "download-queue.get";
    public const string History = "download-queue.history";

    /// <summary>
    /// Non-persistent broadcast published by DataBridge whenever a download job's state changes.
    /// The authoritative record stays the <c>state</c> column; this is a live notification only
    /// (no JetStream, no replay). WebAPI's queue hub fans it into the SSE streams as a state event.
    /// </summary>
    public const string StateChanged = "download-queue.state-changed";

    /// <summary>Queue group so a single DataBridge instance answers each query.</summary>
    public const string QueueGroup = "databridge-download-queue";
}

/// <summary>
/// Live notification that a download job transitioned to a new lifecycle state. Non-persistent —
/// clients treat it as a delta on top of the authoritative snapshot from <c>GET /queue</c>.
/// </summary>
public sealed record DownloadQueueStateChanged
{
    public required Guid JobId { get; init; }
    public required DownloadJobState State { get; init; }
    public required DownloadJobState PreviousState { get; init; }
    public Guid CorrelationId { get; init; }
    public required Instant OccurredAt { get; init; }
}

/// <summary>Sort order for the queue list. Default view is newest-first.</summary>
public enum DownloadQueueSort
{
    /// <summary>Newest jobs first (created_at desc). Default.</summary>
    CreatedAtDesc = 0,
    /// <summary>Highest priority first (priority desc, then created_at desc). For the "queued" view.</summary>
    Priority = 1
}

/// <summary>
/// Request for a paged slice of the full download-job history with optional filters
/// (NATS Core request/reply). Progress is intentionally excluded — it is live-only.
/// </summary>
public sealed record DownloadQueueListRequest
{
    public DownloadJobState? State { get; init; }
    public DownloadSourceKind? SourceKind { get; init; }
    public string? RequestedBy { get; init; }
    public string? StorageKey { get; init; }
    public Instant? CreatedFrom { get; init; }
    public Instant? CreatedTo { get; init; }

    /// <summary>Free-text substring match against the source URL.</summary>
    public string? Query { get; init; }

    public DownloadQueueSort Sort { get; init; } = DownloadQueueSort.CreatedAtDesc;

    /// <summary>Requested page size. The read model clamps this to a safe range.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Opaque continuation token returned by a previous page; null for the first page.</summary>
    public string? Cursor { get; init; }
}

public sealed record DownloadQueueListResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<DownloadQueueJobDto> Items { get; init; } = [];

    /// <summary>Opaque token to pass as <see cref="DownloadQueueListRequest.Cursor"/> for the next page; null when exhausted.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total number of jobs matching the filters (across all pages).</summary>
    public int TotalCount { get; init; }
}

public sealed record DownloadQueueGetRequest
{
    public required Guid JobId { get; init; }
}

public sealed record DownloadQueueGetResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DownloadQueueJobDto? Job { get; init; }
}

public sealed record DownloadQueueHistoryRequest
{
    public required Guid JobId { get; init; }
}

public sealed record DownloadQueueHistoryResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<DownloadQueueHistoryEntryDto> Entries { get; init; } = [];
}

/// <summary>
/// Snapshot of one download job for the admin queue surface. Deliberately omits any live
/// progress fields — progress is delivered only over the SSE routes.
/// </summary>
public sealed record DownloadQueueJobDto
{
    public required Guid JobId { get; init; }
    public Guid CorrelationId { get; init; }
    public DownloadJobState State { get; init; }
    public required string SourceUrl { get; init; }
    public string? RequestedBy { get; init; }
    public string? StorageKey { get; init; }
    public DownloadSourceKind SourceKind { get; init; }
    public int Priority { get; init; }

    public int AttemptMetadata { get; init; }
    public int AttemptDownload { get; init; }
    public int AttemptUpload { get; init; }

    public long? FileSizeBytes { get; init; }
    public string? ContentHashXxh128 { get; init; }

    public FailureKind? FailureKind { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }

    public Instant? ProviderHaltRetryAt { get; init; }
    public Instant? ProviderHaltRetryDispatchedAt { get; init; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
    public Instant? CompletedAt { get; init; }
}

/// <summary>One persisted event on a job's timeline, from <c>downloads.download_job_history</c>.</summary>
public sealed record DownloadQueueHistoryEntryDto
{
    public long Id { get; init; }
    public Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required string EventName { get; init; }
    public string? PayloadJson { get; init; }
    public Instant RecordedAt { get; init; }
}
