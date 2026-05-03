using NodaTime;

namespace Shared.Messaging;

/// <summary>
/// Common envelope carried by every message on the download flow's NATS subjects. The
/// dedupe / correlation / retry machinery in DataBridge, Worker, and Cleipnir relies on
/// these fields being populated consistently across producers.
/// </summary>
public interface IFlowMessage
{
    /// <summary>
    /// Stable identifier for the download job. Every command and event for the same job —
    /// across all stages — carries the same value. Primary correlation key inside the flow.
    /// </summary>
    Guid JobId { get; init; }

    /// <summary>
    /// End-to-end trace id for the user-visible request. May span multiple jobs (for
    /// example, a playlist ingestion that fans out into one job per video).
    /// </summary>
    Guid CorrelationId { get; init; }

    /// <summary>
    /// <see cref="MessageId"/> of the message that produced this one. Builds a causal chain
    /// (e.g. <see cref="MetadataFetched"/>.<c>CausationId</c> equals the originating
    /// <see cref="FetchMetadataCommand"/>.<c>MessageId</c>). Null only on the root of a chain.
    /// </summary>
    Guid? CausationId { get; init; }

    /// <summary>
    /// Unique identifier for THIS message instance. Sent as the JetStream <c>Nats-Msg-Id</c>
    /// header and used as the dedupe key by DataBridge's <c>processed_messages</c> table.
    /// MUST be derived deterministically when republishing on redelivery — see the dedupe
    /// caveat on <see cref="Worker"/>'s <c>DownloadCommandsConsumerService</c>.
    /// </summary>
    Guid MessageId { get; init; }

    /// <summary>
    /// Stable, semantic dedupe key consumed by Cleipnir as a secondary guard
    /// (e.g. <c>"{cmd.OperationKey}/result"</c>). Identical across redeliveries and across
    /// producer restarts; survives even when <see cref="MessageId"/> drift would slip through.
    /// </summary>
    string OperationKey { get; init; }

    /// <summary>
    /// Wall-clock instant the message was produced, in UTC. Sourced from
    /// <see cref="IClock"/> in services so tests can substitute a deterministic clock.
    /// </summary>
    Instant OccurredAt { get; init; }

    /// <summary>
    /// 1-based attempt counter for this stage. Incremented when the saga retries a command
    /// after a transient failure. Distinct from JetStream's per-delivery redelivery counter,
    /// which counts only un-acked redeliveries of the same physical message.
    /// </summary>
    int Attempt { get; init; }
}

/// <summary>
/// Lifecycle states for a download job, persisted by DataBridge alongside the saga state.
/// Numeric values are part of the wire/database contract — never renumber existing entries.
/// </summary>
public enum DownloadJobState
{
    /// <summary>Job row exists; no commands have been emitted yet.</summary>
    Queued = 0,
    /// <summary><see cref="FetchMetadataCommand"/> has been published; awaiting the matching event.</summary>
    MetadataPending = 1,
    /// <summary><see cref="MetadataFetched"/> arrived and was committed.</summary>
    MetadataResolved = 2,
    /// <summary><see cref="DownloadVideoCommand"/> has been published; awaiting completion.</summary>
    DownloadPending = 3,
    /// <summary>Bytes are on the worker's local disk under the temp-file ref.</summary>
    DownloadedTemp = 4,
    /// <summary><see cref="UploadObjectCommand"/> has been published; awaiting upload completion.</summary>
    UploadPending = 5,
    /// <summary>Bytes are committed to the final storage backend.</summary>
    Uploaded = 6,
    /// <summary>Final DB commit (movie + replica row) is in progress.</summary>
    CommitPending = 7,
    /// <summary>End state — everything written, temp cleaned, replica visible to readers.</summary>
    Completed = 8,
    /// <summary>A failure triggered a compensating workflow (delete temp / uploaded object).</summary>
    Compensating = 9,
    /// <summary>Last attempt failed transiently; saga may retry on its retry budget.</summary>
    FailedTransient = 10,
    /// <summary>Last attempt failed permanently (source removed, geo-block, auth failure); will not retry.</summary>
    FailedPermanent = 11,
    /// <summary>Retry budget exhausted; routed to the DLQ and requires manual intervention.</summary>
    DeadLettered = 12,
    /// <summary>Metadata matched a previously completed source version; download was skipped.</summary>
    AlreadyDownloaded = 13
}

/// <summary>
/// Classification of a failure for retry / compensation routing. Producers should use
/// <see cref="Permanent"/> only when re-running the same command is guaranteed to fail.
/// </summary>
public enum FailureKind
{
    /// <summary>Cause is unknown; treat conservatively (usually as transient).</summary>
    Unknown = 0,
    /// <summary>Likely to succeed on retry — network blip, rate limit, server-side 5xx.</summary>
    Transient = 1,
    /// <summary>Will not succeed on retry — source removed, geo-block, auth failure, malformed input.</summary>
    Permanent = 2,
    /// <summary>The operation exceeded its time budget. Generally retried with longer timeout.</summary>
    Timeout = 3,
    /// <summary>The operation was cancelled (host shutdown, user-initiated cancel).</summary>
    Cancelled = 4
}

/// <summary>
/// Root event for the download flow. Published by WebAPI when a user requests an ingestion;
/// DataBridge consumes it, opens a Cleipnir flow, and emits a <see cref="FetchMetadataCommand"/>.
/// </summary>
public sealed record DownloadRequested : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <summary>Always <c>1</c> for the root request — retries open a new job rather than re-issuing this event.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// Source URL to ingest. Whatever yt-dlp's extractor surface accepts: YouTube link,
    /// Twitch VOD, direct media URL, podcast feed entry, etc.
    /// </summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Optional principal who initiated the request (user id, API token id). Audit only —
    /// the flow does not branch on it.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Target FluentStorage backend key (e.g. <c>"local-nas"</c>, <c>"aws-s3"</c>). Resolved
    /// at upload time by the storage provider. <see langword="null"/> means "use the system default".
    /// </summary>
    public string? StorageKey { get; init; }

    /// <summary>Free-form tags to attach to the resulting movie row (collections, genre hints, etc.).</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// When true, bypasses source-metadata duplicate fast-fail and continues to download bytes
    /// so hosts that replace media under the same source metadata can be revalidated.
    /// </summary>
    public bool ForceDownload { get; init; }
}

/// <summary>
/// Command to the Worker telling it to invoke yt-dlp for source-only metadata (no media bytes
/// downloaded). Reply: <see cref="MetadataFetched"/> on success, <see cref="MetadataFetchFailed"/> on failure.
/// </summary>
public sealed record FetchMetadataCommand : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>The original source URL passed through from <see cref="DownloadRequested.SourceUrl"/>.</summary>
    public required string SourceUrl { get; init; }
}

/// <summary>
/// Event emitted by the Worker once yt-dlp resolves the source metadata. Consumed by
/// DataBridge to upsert the movie row, decide on dedupe, and emit a <see cref="DownloadVideoCommand"/>.
/// </summary>
public sealed record MetadataFetched : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Deterministic XxHash128 over provider + source media id + source last-modified.</summary>
    public string? SourceMetadataHash { get; init; }

    /// <summary>Provider-specific source media id (e.g. YouTube video id <c>"dQw4w9WgXcQ"</c>).</summary>
    public string? SourceMediaId { get; init; }

    /// <summary>yt-dlp extractor/provider name (e.g. <c>"youtube"</c>, <c>"twitch:vod"</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>Source-reported last-modified instant, when available from metadata.</summary>
    public Instant? SourceLastModified { get; init; }

    /// <summary>Display title from the source. Used for movie naming; not a stable identifier.</summary>
    public string? Title { get; init; }

    /// <summary>Channel / creator display name. Audit and search only.</summary>
    public string? Uploader { get; init; }

    /// <summary>
    /// Stable archive identifier used to namespace the worker's temp directory and the final
    /// object key (e.g. <c>"youtube/dQw4w9WgXcQ"</c>). Falls back to <c>JobId.ToString("N")</c>
    /// when provider/source-id are absent.
    /// </summary>
    public required string ArchiveKey { get; init; }
}

/// <summary>
/// Event emitted by the Worker when metadata extraction failed. Drives the saga's
/// retry / fail decision based on <see cref="FailureKind"/>.
/// </summary>
public sealed record MetadataFetchFailed : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Whether the failure is retryable. Maps to the saga's transient/permanent split.</summary>
    public required FailureKind FailureKind { get; init; }

    /// <summary>Optional structured error code (e.g. <c>"GEO_BLOCKED"</c>, <c>"PRIVATE_VIDEO"</c>) for ops dashboards.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error text — typically the exception message or yt-dlp stderr tail.</summary>
    public required string ErrorMessage { get; init; }
}

/// <summary>
/// Command telling the Worker to download the source media into a worker-local temp file.
/// Reply: <see cref="DownloadCompleted"/> on success, <see cref="DownloadFailed"/> on failure.
/// </summary>
public sealed record DownloadVideoCommand : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Source URL to download (passed through from <see cref="DownloadRequested.SourceUrl"/>).</summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Archive identifier from <see cref="MetadataFetched.ArchiveKey"/>; the worker uses it to
    /// namespace its temp directory.
    /// </summary>
    public required string ArchiveKey { get; init; }
}

/// <summary>
/// Event emitted when the source media is fully written to a worker-local temp file and its
/// hash is computed.
/// </summary>
public sealed record DownloadCompleted : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>
    /// Absolute path on the producing worker where the bytes live. Worker-local — not visible
    /// to other services. Cleaned up by <see cref="DeleteTempFileCommand"/>.
    /// </summary>
    public required string TempFileRef { get; init; }

    /// <summary>Filename component (no directory) used for the eventual object key suffix.</summary>
    public required string FileName { get; init; }

    /// <summary>Total bytes written. Cross-checked against <see cref="UploadCompleted.ContentLengthBytes"/> at commit.</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// Hex-encoded XxHash128 of the file contents. Content-addressed dedupe key when deciding
    /// whether to skip the upload to backends that already host this hash. Null only if hashing
    /// was skipped (should not happen in production).
    /// </summary>
    public string? ContentHashXxh128 { get; init; }

    /// <summary>MIME type inferred from yt-dlp output / file probing (e.g. <c>"video/mp4"</c>).</summary>
    public string? ContentType { get; init; }
}

/// <summary>
/// Event emitted when the download failed. Carries the temp-file ref so the saga can fire a
/// cleanup command for any partial bytes left on disk.
/// </summary>
public sealed record DownloadFailed : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Whether the failure is retryable.</summary>
    public required FailureKind FailureKind { get; init; }

    /// <summary>Optional structured error code for ops dashboards.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error text (exception message or yt-dlp stderr tail).</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Path to any partial bytes left on disk; cleanup compensation will target it. Null when nothing was written.</summary>
    public string? TempFileRef { get; init; }
}

/// <summary>
/// Command telling the Worker to upload the temp file to the requested storage backend.
/// Reply: <see cref="UploadCompleted"/> on success, <see cref="UploadFailed"/> on failure.
/// </summary>
public sealed record UploadObjectCommand : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Source path on the worker — must equal <see cref="DownloadCompleted.TempFileRef"/>.</summary>
    public required string TempFileRef { get; init; }

    /// <summary>
    /// FluentStorage backend key (e.g. <c>"local-nas"</c>, <c>"aws-s3"</c>) to upload into.
    /// Resolved by the storage provider on the worker.
    /// </summary>
    public required string StorageKey { get; init; }

    /// <summary>Archive identifier used to construct the final object key (e.g. <c>"youtube/dQw4w9WgXcQ"</c>).</summary>
    public required string ArchiveKey { get; init; }
}

/// <summary>
/// Event emitted when the upload is durable in the backend. Carries the canonical location
/// (<see cref="StorageKey"/>, <see cref="ObjectKey"/>, optional <see cref="StorageVersion"/>)
/// and the verification material (hash + length) DataBridge uses to commit the replica row.
/// </summary>
public sealed record UploadCompleted : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>The temp-file source the bytes came from. Subsequently fed to <see cref="DeleteTempFileCommand"/>.</summary>
    public required string TempFileRef { get; init; }

    /// <summary>Backend key the bytes were written to.</summary>
    public required string StorageKey { get; init; }

    /// <summary>
    /// Backend-relative object path (e.g. <c>"archives/youtube/dQw4w9WgXcQ/video.bin"</c>).
    /// Combined with <see cref="StorageKey"/> uniquely identifies the replica.
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Backend-supplied version id when the backend supports versioning (S3 <c>VersionId</c>,
    /// Azure <c>ETag</c>). Null on backends that do not version.
    /// </summary>
    public string? StorageVersion { get; init; }

    /// <summary>
    /// Hex XxHash128 of the bytes as observed at upload time. Should match
    /// <see cref="DownloadCompleted.ContentHashXxh128"/> — divergence indicates corruption in
    /// transit and aborts the commit.
    /// </summary>
    public string? ContentHashXxh128 { get; init; }

    /// <summary>Bytes durably stored. Should match <see cref="DownloadCompleted.FileSizeBytes"/>.</summary>
    public long? ContentLengthBytes { get; init; }
}

/// <summary>
/// Event emitted when the upload failed. Carries the temp-file ref so a retry can re-upload
/// without re-downloading.
/// </summary>
public sealed record UploadFailed : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Whether the failure is retryable.</summary>
    public required FailureKind FailureKind { get; init; }

    /// <summary>Optional structured error code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error text.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Source path on the worker; retained so a retry can re-upload without re-downloading.</summary>
    public string? TempFileRef { get; init; }
}

/// <summary>
/// Command telling the Worker to delete the worker-local temp file. Issued after a successful
/// upload-and-commit, OR as compensation for a failed download.
/// </summary>
public sealed record DeleteTempFileCommand : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Absolute path on the worker to remove.</summary>
    public required string TempFileRef { get; init; }
}

/// <summary>Event acknowledging that the temp file has been removed (or was never present).</summary>
public sealed record TempFileDeleted : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>The path that was removed.</summary>
    public required string TempFileRef { get; init; }
}

/// <summary>
/// Event when the temp-file delete failed. Generally treated as advisory — the saga continues
/// and a periodic janitor sweeps stragglers, since orphan temp files are local to one worker
/// and not visible to readers.
/// </summary>
public sealed record TempFileDeleteFailed : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>The path that could not be removed.</summary>
    public required string TempFileRef { get; init; }

    /// <summary>Whether the failure is retryable.</summary>
    public required FailureKind FailureKind { get; init; }

    /// <summary>Human-readable error text.</summary>
    public required string ErrorMessage { get; init; }
}

/// <summary>
/// Compensating command issued when an upload succeeded but a downstream step failed:
/// removes the orphaned object from the final storage backend so storage costs do not leak.
/// </summary>
public sealed record DeleteUploadedObjectCommand : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Backend key that holds the orphaned object.</summary>
    public required string StorageKey { get; init; }

    /// <summary>Backend-relative object path to remove.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// Backend version id to target (S3 <c>VersionId</c>, etc.) when versioning is on.
    /// Null = delete the latest version, or the unversioned object on backends without versioning.
    /// </summary>
    public string? StorageVersion { get; init; }
}

/// <summary>Event acknowledging the compensating delete on the final backend.</summary>
public sealed record UploadedObjectDeleted : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Backend the object was removed from.</summary>
    public required string StorageKey { get; init; }

    /// <summary>The object path that was removed.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>The specific version that was removed when versioning was used.</summary>
    public string? StorageVersion { get; init; }
}

/// <summary>
/// Event when the compensating delete failed — the orphan is still in the backend. Triggers a
/// dead-letter / on-call alert because the object is now unreferenced and will accrue cost.
/// </summary>
public sealed record UploadedObjectDeleteFailed : IFlowMessage
{
    /// <inheritdoc />
    public required Guid JobId { get; init; }
    /// <inheritdoc />
    public required Guid CorrelationId { get; init; }
    /// <inheritdoc />
    public Guid? CausationId { get; init; }
    /// <inheritdoc />
    public required Guid MessageId { get; init; }
    /// <inheritdoc />
    public required string OperationKey { get; init; }
    /// <inheritdoc />
    public required Instant OccurredAt { get; init; }
    /// <inheritdoc />
    public required int Attempt { get; init; }

    /// <summary>Backend that still holds the orphan.</summary>
    public required string StorageKey { get; init; }

    /// <summary>The orphaned object path.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>The version that could not be removed when versioning was used.</summary>
    public string? StorageVersion { get; init; }

    /// <summary>Whether the failure is retryable.</summary>
    public required FailureKind FailureKind { get; init; }

    /// <summary>Human-readable error text.</summary>
    public required string ErrorMessage { get; init; }
}
