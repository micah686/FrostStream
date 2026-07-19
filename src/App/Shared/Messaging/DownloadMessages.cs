using NodaTime;
using Shared.Metadata;
using YtDlpSharpLib.Options;

namespace Shared.Messaging;

/// <summary>
/// What kind of media a download job should produce. Audio jobs cause the Worker to
/// force <c>--extract-audio</c> + an audio output template; video jobs use the default
/// merge/recode pipeline.
/// </summary>
public enum MediaKind
{
    /// <summary>Default: download video (with audio muxed in).</summary>
    Video = 0,
    /// <summary>Audio-only: yt-dlp extracts audio and converts to <see cref="AudioConversionFormat"/>.</summary>
    Audio = 1
}

/// <summary>
/// Where a download request came from. Collection children retain this value so the UI and
/// group controls can distinguish direct, playlist, and channel work.
/// </summary>
public enum DownloadSourceKind
{
    Direct = 0,
    Playlist = 1,
    Channel = 2
}

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
/// Legacy V1 lifecycle values retained only for the existing database column and historical
/// records. Download Flow V2 uses <see cref="DownloadJobStatus"/> plus
/// <see cref="DownloadStage"/>; new orchestration must not branch on this enum.
/// Numeric values are part of the database contract — never renumber existing entries.
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
    /// <summary>Source already downloaded (matched an existing version); download was skipped.</summary>
    AlreadyDownloaded = 13,
    /// <summary>Legacy V1 slot-waiting state. Download Flow V2 does not use a slot coordinator.</summary>
    DownloadQueued = 14,
    /// <summary>A user requested cancellation; the flow is stopping any in-flight work and cleanup.</summary>
    Cancelling = 15,
    /// <summary>End state — the user cancelled the job before it completed.</summary>
    Cancelled = 16,
    /// <summary>Suppressed by a config-set ignore keyword during a user-initiated playlist/channel
    /// download; no download was started. Force-queueing transitions it back to <see cref="Queued"/>.</summary>
    Ignored = 17,
    /// <summary>Provider access was halted after bot-detection or anti-automation challenge was detected.</summary>
    ProviderHalted = 18
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
    /// <summary>Legacy cancellation classification. V2 uses <see cref="Stopped"/> for user stops.</summary>
    Cancelled = 4,
    /// <summary>The coordinating service or worker disappeared while a run was active.</summary>
    Interrupted = 5,
    /// <summary>A provider circuit prevented execution.</summary>
    ProviderBlocked = 6,
    /// <summary>The user explicitly stopped the job.</summary>
    Stopped = 7
}

/// <summary>
/// Root event for the download flow. Published by WebAPI when a user requests an ingestion;
/// DataBridge consumes it, opens a Cleipnir flow, and emits a <see cref="FetchMetadataCommand"/>.
/// </summary>
public sealed record DownloadRequested : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// Source URL to ingest. Whatever yt-dlp's extractor surface accepts: YouTube link,
    /// Twitch VOD, direct media URL, podcast feed entry, etc.
    /// </summary>
    public required string SourceUrl { get; init; }

    /// <summary>Optional principal who initiated the request. Audit only.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Target FluentStorage backend key (e.g. <c>"local-nas"</c>, <c>"aws-s3"</c>).
    /// <see langword="null"/> means "use the system default".
    /// </summary>
    public string? StorageKey { get; init; }

    /// <summary>Free-form tags to attach to the resulting movie row.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// When true, bypasses dedupe-by-source fast-fail and continues to download bytes
    /// so hosts that replace media under the same source identity can be revalidated.
    /// </summary>
    public bool ForceDownload { get; init; }

    /// <summary>Where this request came from for grouping, filtering, and display.</summary>
    public DownloadSourceKind SourceKind { get; init; } = DownloadSourceKind.Direct;

    /// <summary>
    /// What kind of media to produce (video or audio-only). Audio jobs force
    /// <c>--extract-audio</c> at the Worker.
    /// </summary>
    public MediaKind MediaKind { get; init; } = MediaKind.Video;

    /// <summary>
    /// Audio output format used when <see cref="MediaKind"/> is <see cref="MediaKind.Audio"/>.
    /// Ignored for video jobs. <see langword="null"/> defers to yt-dlp's default.
    /// </summary>
    public AudioConversionFormat? AudioFormat { get; init; }

    /// <summary>When true, queue a cached opus audio rendition after the video download completes.</summary>
    public bool EncodeAudioRendition { get; init; }

    /// <summary>
    /// Caller-supplied yt-dlp options snapshot. The Worker merges this on top of its
    /// own defaults before invoking yt-dlp. Mutually exclusive with
    /// <see cref="PresetKey"/> at the API boundary.
    /// </summary>
    public YtDlpOptions? YtDlpOptions { get; init; }

    /// <summary>
    /// Reference to a stored option preset (resolved by DataBridge from the
    /// <c>download_option_presets</c> table). DataBridge populates
    /// <see cref="YtDlpOptions"/> from the preset before commands are dispatched.
    /// </summary>
    public string? PresetKey { get; init; }

    /// <summary>
    /// Opaque, fully-resolved OpenBAO path to a user-owned cookie profile
    /// (<c>cookies/users/{subject}/{profileKey}</c>), resolved by WebAPI from the authenticated
    /// user. The Worker fetches the secret, materializes it to a temp file, and passes
    /// <c>--cookies &lt;path&gt;</c> to yt-dlp for the duration of the run; it only ever sees this
    /// reference, never the cookie body or the owning user's identity beyond the path.
    /// </summary>
    public string? CookieSecretPath { get; init; }

    /// <summary>
    /// Administrative priority: 0 (default / lowest) to 100 (highest). It is retained on
    /// the job and used by priority-sorted queue views without changing V2 restart semantics.
    /// </summary>
    public int Priority { get; init; } = 0;

    /// <summary>When true, the Worker passes <c>--write-comments</c> to yt-dlp so comments are fetched,
    /// persisted to the database, and indexed in Typesense.</summary>
    public bool FetchComments { get; init; } = false;
}

/// <summary>
/// Command to the Worker telling it to invoke yt-dlp for source-only metadata.
/// Reply: <see cref="MetadataFetched"/> on success, <see cref="MetadataFetchFailed"/> on failure.
/// </summary>
public sealed record FetchMetadataCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>The original source URL passed through from <see cref="DownloadRequested.SourceUrl"/>.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Target FluentStorage backend key. The Worker probes this backend for connectivity
    /// before running yt-dlp so that a misconfigured or unreachable backend fails fast
    /// rather than after a potentially lengthy metadata fetch.
    /// </summary>
    public required string StorageKey { get; init; }

    /// <summary>
    /// Worker tag that was used to route this command. Informational; the routing already
    /// happened at publish time via the subject suffix
    /// (<c>download.v2.command.metadata.fetch.{tag}</c>).
    /// </summary>
    public string? RequiredWorkerTag { get; init; }

    /// <summary>
    /// Caller-supplied yt-dlp options snapshot, passed through to the Worker for the
    /// metadata-fetch invocation. Cookies-from-browser, custom user-agent, etc. all
    /// matter at metadata time, so we don't strip the options here.
    /// </summary>
    public YtDlpOptions? YtDlpOptions { get; init; }

    /// <summary>Same resolved user-owned cookie path as on <see cref="DownloadRequested.CookieSecretPath"/>.</summary>
    public string? CookieSecretPath { get; init; }

    /// <summary>When true, the Worker passes <c>--write-comments</c> to yt-dlp so comments are included in
    /// the returned metadata and subsequently persisted to the database and indexed in Typesense.</summary>
    public bool FetchComments { get; init; } = false;
}

/// <summary>
/// Event emitted by the Worker once yt-dlp resolves the source metadata. DataBridge uses
/// <see cref="Provider"/> + <see cref="SourceMediaId"/> + <see cref="SourceLastModified"/>
/// to decide whether the source is already on file (dedupe-by-source).
/// </summary>
public sealed record MetadataFetched : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>Provider-specific source media id (e.g. YouTube video id <c>"dQw4w9WgXcQ"</c>).</summary>
    public string? SourceMediaId { get; init; }

    /// <summary>yt-dlp extractor/provider name (e.g. <c>"youtube"</c>, <c>"twitch:vod"</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>Source-reported last-modified instant, when available from metadata.</summary>
    public Instant? SourceLastModified { get; init; }

    /// <summary>Display title from the source. Audit/search only — not a stable identifier.</summary>
    public string? Title { get; init; }

    /// <summary>Channel / creator display name. Audit and search only.</summary>
    public string? Uploader { get; init; }

    /// <summary>Small source metadata snapshot used to generate the archive .meta sidecar.</summary>
    public MetaFile? MetaFile { get; init; }
}

/// <summary>
/// Event emitted by the Worker when metadata extraction failed. Drives the saga's
/// retry / fail decision based on <see cref="FailureKind"/>.
/// </summary>
public sealed record MetadataFetchFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    public required FailureKind FailureKind { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>Provider whose access failed, when known (for example, <c>youtube</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>When true, the producer recommends halting more downloads from <see cref="Provider"/>.</summary>
    public bool HaltProviderDownloads { get; init; }

    public required string ErrorMessage { get; init; }
}

/// <summary>
/// Command telling the Worker to download the source media into a worker-local temp file.
/// Reply: <see cref="DownloadCompleted"/> on success, <see cref="DownloadFailed"/> on failure.
/// </summary>
public sealed record DownloadVideoCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>Source URL to download (passed through from <see cref="DownloadRequested.SourceUrl"/>).</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Worker tag used to route this command. Informational.</summary>
    public string? RequiredWorkerTag { get; init; }

    /// <summary>What kind of media to produce (video or audio-only).</summary>
    public MediaKind MediaKind { get; init; } = MediaKind.Video;

    /// <summary>Audio output format used when <see cref="MediaKind"/> is <see cref="MediaKind.Audio"/>.</summary>
    public AudioConversionFormat? AudioFormat { get; init; }

    /// <summary>Caller-supplied yt-dlp options snapshot, passed through to the Worker.</summary>
    public YtDlpOptions? YtDlpOptions { get; init; }

    /// <summary>Same resolved user-owned cookie path as on <see cref="DownloadRequested.CookieSecretPath"/>.</summary>
    public string? CookieSecretPath { get; init; }

}

/// <summary>
/// Event emitted when the source media is fully written to a worker-local temp file and its
/// hash is computed. DataBridge uses the hash to reserve a content-version row before
/// dispatching the upload command.
/// </summary>
public sealed record DownloadCompleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>Absolute path on the producing worker where the bytes live. Worker-local.</summary>
    public required string TempFileRef { get; init; }

    /// <summary>Filename component (no directory) used as the suffix of the final storage path.</summary>
    public required string FileName { get; init; }

    /// <summary>Total bytes written.</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>Hex-encoded XxHash128 of the file contents. Identity for the bytes.</summary>
    public required string ContentHashXxh128 { get; init; }

    /// <summary>
    /// When yt-dlp also produced an <c>.info.json</c> sidecar (because the caller passed
    /// <c>WriteInfoJson = true</c>), the worker reports its temp ref and hash here so the
    /// saga can upload it next to the video. Null when no sidecar was written.
    /// </summary>
    public string? InfoJsonTempFileRef { get; init; }

    /// <summary>Filename of the info.json sidecar (no directory). Null when none.</summary>
    public string? InfoJsonFileName { get; init; }

    /// <summary>Bytes of the info.json sidecar. Null when none.</summary>
    public long? InfoJsonSizeBytes { get; init; }

    /// <summary>Hex-encoded XxHash128 of the info.json sidecar. Null when none.</summary>
    public string? InfoJsonContentHashXxh128 { get; init; }

    /// <summary>
    /// The thumbnail sidecar yt-dlp wrote (because <c>WriteThumbnail</c> was set). The saga
    /// uploads it co-located with the video and records the blob path on
    /// <c>media_metadata.thumbnail_storage_path</c>. Null when none was produced.
    /// </summary>
    public SidecarFileRef? Thumbnail { get; init; }

    /// <summary>
    /// Caption/subtitle sidecars yt-dlp wrote (bounded to the caller's requested
    /// <c>SubLangs</c>). Each is uploaded co-located with the video and recorded as a
    /// <c>metadata.media_captions</c> row. Empty when none were produced.
    /// </summary>
    public IReadOnlyList<SidecarFileRef> Captions { get; init; } = [];

    /// <summary>
    /// Non-fatal problems observed while acquiring the media. The coordinator persists each one
    /// and allows the run to finish as <c>CompletedWithWarnings</c>.
    /// </summary>
    public IReadOnlyList<DownloadStageWarning> Warnings { get; init; } = [];
}

public sealed record DownloadStageWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// A worker-local sidecar file (thumbnail or caption) yt-dlp produced next to the media file.
/// </summary>
public sealed record SidecarFileRef
{
    /// <summary>Absolute worker-local path to the sidecar file.</summary>
    public required string TempFileRef { get; init; }

    /// <summary>Filename (no directory); reused as the co-located blob filename.</summary>
    public required string FileName { get; init; }

    /// <summary>Total bytes written.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Hex-encoded XxHash128 of the sidecar contents.</summary>
    public required string ContentHashXxh128 { get; init; }

    /// <summary>Language code for caption sidecars (e.g. "en", "en-US"); null for thumbnails.</summary>
    public string? LanguageCode { get; init; }

}

/// <summary>
/// What artifact an <see cref="UploadObjectCommand"/>/<see cref="UploadCompleted"/> pair
/// refers to. The flow dispatches the primary upload first, then optionally a second
/// upload for the <c>.info.json</c> sidecar; DataBridge's event-apply layer routes them
/// to different persistence paths.
/// </summary>
public enum UploadArtifactKind
{
    /// <summary>The main media file. Drives version reservation and job state.</summary>
    Primary = 0,
    /// <summary>The yt-dlp <c>.info.json</c> sidecar, written next to the primary.</summary>
    InfoJson = 1,
    /// <summary>The per-media thumbnail image, co-located with the primary.</summary>
    Thumbnail = 2,
    /// <summary>A per-media caption/subtitle track, co-located with the primary.</summary>
    Caption = 3,
    /// <summary>The DataBridge-generated <c>.meta</c> sidecar containing title, hash, media GUID,
    /// and original URL — used to correlate storage objects back to their DB records after
    /// migrations or path changes.</summary>
    Meta = 4,
    /// <summary>
    /// The Worker-generated <c>.comments.json</c> sidecar: the comment thread parsed out of the
    /// info.json and serialized as <c>CapturedCommentMetadata</c> rows. Comment threads are
    /// unbounded, so they travel via storage instead of inline NATS payloads; DataBridge loads
    /// this sidecar back during the rich-metadata write.
    /// </summary>
    Comments = 5
}

/// <summary>
/// Advisory event emitted while yt-dlp is downloading or post-processing media. This is
/// intentionally not part of DataBridge's persisted saga state.
/// </summary>
public sealed record DownloadProgress : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>Monotonic per-download progress sequence assigned by the Worker.</summary>
    public required int Sequence { get; init; }

    /// <summary>Original source URL being downloaded.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>High-level yt-dlp phase, e.g. <c>Downloading</c>, <c>Merging</c>.</summary>
    public required string Phase { get; init; }

    public double? Percent { get; init; }
    public long? DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public string? Speed { get; init; }
    public double? EtaSeconds { get; init; }
    public string? Destination { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Event emitted when the download failed. Carries the temp-file ref so the saga can fire a
/// cleanup command for any partial bytes left on disk.
/// </summary>
public sealed record DownloadFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    /// <summary>Provider whose access failed, when known (for example, <c>youtube</c>).</summary>
    public string? Provider { get; init; }
    /// <summary>When true, the producer recommends halting more downloads from <see cref="Provider"/>.</summary>
    public bool HaltProviderDownloads { get; init; }
    public required string ErrorMessage { get; init; }
    public string? TempFileRef { get; init; }
}

/// <summary>
/// Command telling the Worker to upload the temp file to a specific storage path that
/// DataBridge has already reserved in <c>media_content_id_versions</c>.
/// Reply: <see cref="UploadCompleted"/> on success, <see cref="UploadFailed"/> on failure.
/// </summary>
public sealed record UploadObjectCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>Source path on the worker. Null when <see cref="InlineContent"/> is set instead.</summary>
    public string? TempFileRef { get; init; }

    /// <summary>
    /// Raw bytes for the worker to write directly to <see cref="StoragePath"/>. Used for
    /// DataBridge-generated sidecars (e.g. <c>.meta</c> files) where no worker-local temp file
    /// exists. Exactly one of <see cref="TempFileRef"/> and <see cref="InlineContent"/> must be set.
    /// </summary>
    public byte[]? InlineContent { get; init; }

    /// <summary>Worker tag used to route this command. Informational.</summary>
    public string? RequiredWorkerTag { get; init; }

    /// <summary>FluentStorage backend key to upload into.</summary>
    public required string StorageKey { get; init; }

    /// <summary>
    /// Final, backend-relative path the worker must write to (e.g.
    /// <c>"archives/{mediaGuid}/v1/video.mp4"</c>). Computed by DataBridge during version
    /// reservation, so the worker has no path-construction logic of its own.
    /// </summary>
    public required string StoragePath { get; init; }

    /// <summary>Hex XxHash128 of the bytes; passed through so the upload event can reaffirm it.</summary>
    public required string ContentHashXxh128 { get; init; }

    /// <summary>
    /// When true, the worker hashes bytes as the upload stream is read and fails the command
    /// if the observed hash differs from <see cref="ContentHashXxh128"/>.
    /// </summary>
    public bool VerifyHashWhileStreaming { get; init; }

    /// <summary>
    /// Which artifact this upload represents. Defaults to <see cref="UploadArtifactKind.Primary"/>
    /// for backwards compatibility with callers that don't care about sidecars.
    /// </summary>
    public UploadArtifactKind Kind { get; init; } = UploadArtifactKind.Primary;
}

/// <summary>
/// Event emitted when the upload is durable in the backend.
/// </summary>
public sealed record UploadCompleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>The temp-file source the bytes came from. Null for inline-content uploads (e.g. <c>.meta</c> sidecars).</summary>
    public string? TempFileRef { get; init; }

    /// <summary>Backend key the bytes were written to.</summary>
    public required string StorageKey { get; init; }

    /// <summary>Backend-relative object path, equal to the <see cref="UploadObjectCommand.StoragePath"/> the worker received.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Backend-supplied version id when the backend supports versioning. Null when unsupported.</summary>
    public string? StorageVersion { get; init; }

    /// <summary>Hex XxHash128 of the bytes as observed at upload time.</summary>
    public required string ContentHashXxh128 { get; init; }

    /// <summary>Bytes durably stored.</summary>
    public long? ContentLengthBytes { get; init; }

    /// <summary>Mirrors the dispatching command's <see cref="UploadObjectCommand.Kind"/>.</summary>
    public UploadArtifactKind Kind { get; init; } = UploadArtifactKind.Primary;
}

/// <summary>
/// Event emitted when the upload failed. Carries the temp-file ref so a retry can re-upload
/// without re-downloading.
/// </summary>
public sealed record UploadFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
    public string? TempFileRef { get; init; }

    /// <summary>Mirrors the dispatching command's <see cref="UploadObjectCommand.Kind"/>.</summary>
    public UploadArtifactKind Kind { get; init; } = UploadArtifactKind.Primary;
}

/// <summary>
/// Command telling the Worker to delete the worker-local temp file. Issued after a successful
/// upload-and-commit, OR as compensation for a failed download.
/// </summary>
public sealed record DeleteTempFileCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>Worker tag used to route this command. Informational.</summary>
    public string? RequiredWorkerTag { get; init; }

    public required string TempFileRef { get; init; }
}

/// <summary>Event acknowledging that the temp file has been removed (or was never present).</summary>
public sealed record TempFileDeleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    public required string TempFileRef { get; init; }
}

/// <summary>Event when the temp-file delete failed. Treated as advisory.</summary>
public sealed record TempFileDeleteFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    public required string TempFileRef { get; init; }
    public required FailureKind FailureKind { get; init; }
    public required string ErrorMessage { get; init; }
}

// ── NATS Core request/reply — download admin ────────────────────────────────────

/// <summary>Request to update a job's stored administrative priority (NATS Core request/reply).</summary>
public sealed record UpdateDownloadPriorityRequest
{
    public required Guid JobId { get; init; }
    /// <summary>New priority value 0–100.</summary>
    public required int Priority { get; init; }
}

/// <summary>Response to <see cref="UpdateDownloadPriorityRequest"/>.</summary>
public sealed record UpdateDownloadPriorityResponse
{
    public bool Success { get; init; }
    /// <summary>When false, explanation of why the update was not applied.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Compensating command issued when an upload succeeded but a downstream step failed:
/// removes the orphaned object from the final storage backend.
/// </summary>
public sealed record DeleteUploadedObjectCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    /// <summary>Worker tag used to route this command. Informational.</summary>
    public string? RequiredWorkerTag { get; init; }

    public required string StorageKey { get; init; }

    /// <summary>Backend-relative path to remove (matches <see cref="UploadCompleted.StoragePath"/>).</summary>
    public required string StoragePath { get; init; }

    public string? StorageVersion { get; init; }
}

/// <summary>Event acknowledging the compensating delete on the final backend.</summary>
public sealed record UploadedObjectDeleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
    public string? StorageVersion { get; init; }
}

/// <summary>
/// Event when the compensating delete failed — the orphan is still in the backend.
/// </summary>
public sealed record UploadedObjectDeleteFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public DownloadExecutionIdentity? Execution { get; init; }

    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
    public string? StorageVersion { get; init; }
    public required FailureKind FailureKind { get; init; }
    public required string ErrorMessage { get; init; }
}
