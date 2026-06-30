using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

/// <summary>
/// Persistence façade for the download orchestration. Scoped — resolve through
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/> from singletons
/// (e.g. ingress hosted services and the Cleipnir flow).
/// </summary>
public interface IDownloadJobsRepository
{
    Task<bool> IsMessageProcessedAsync(Guid messageId, CancellationToken ct = default);

    Task<bool> TryMarkMessageProcessedAsync(Guid messageId, string operationKey, Guid jobId, CancellationToken ct = default);

    Task MarkMessageProcessedAsync(Guid messageId, string operationKey, Guid jobId, CancellationToken ct = default);

    Task CreateJobIfMissingAsync(DownloadRequested request, CancellationToken ct = default);

    Task<DownloadRequested?> GetOriginalRequestAsync(Guid jobId, CancellationToken ct = default);

    Task<MetadataFetched?> GetLastMetadataFetchedAsync(Guid jobId, CancellationToken ct = default);

    Task UpdateStateAsync(Guid jobId, DownloadJobState state, CancellationToken ct = default);

    Task ApplyMetadataAsync(Guid jobId, MetadataFetched evt, CancellationToken ct = default);

    /// <summary>
    /// Looks up an existing source row by (provider, source_media_id). Decides whether
    /// the saga can fast-fail because we already have this exact source version.
    /// </summary>
    Task<SourceVersionDecision> CheckSourceVersionAsync(MetadataFetched evt, bool forceDownload, CancellationToken ct = default);

    Task MarkAlreadyDownloadedAsync(Guid jobId, Guid mediaGuid, CancellationToken ct = default);

    /// <summary>
    /// Deletes a row from <c>media_content_id_versions</c> after upload/commit failure so a
    /// future job that re-downloads the same bytes doesn't reuse a path that has no bytes
    /// at it. No-op if the row is missing.
    /// </summary>
    Task DeleteReservedVersionAsync(Guid mediaGuid, int versionNum, CancellationToken ct = default);

    /// <summary>
    /// Removes the <c>media_source_versions</c> row for the given provider/source identity and
    /// then deletes the <c>public.media</c> root row (CASCADE removes any partially-written
    /// <c>metadata.*</c> rows). Only called when the <c>media_guid</c> was freshly minted by
    /// this job and no prior content existed under it.
    /// </summary>
    Task DeleteNewMediaGuidAsync(Guid mediaGuid, string? provider, string? sourceMediaId, CancellationToken ct = default);

    Task ApplyDownloadCompletedAsync(Guid jobId, DownloadCompleted evt, CancellationToken ct = default);

    /// <summary>
    /// Reserves a content-version row for the bytes that just landed on a worker's temp
    /// disk. Implements option-(a) merge: if the same (storage_key, content_hash) already
    /// exists, the new source is mapped to that existing media_guid and no new version
    /// row is inserted.
    /// </summary>
    Task<VersionReservation> ReserveVersionAsync(VersionReservationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Flips the job to <see cref="DownloadJobState.Uploaded"/> and points the source row's
    /// <c>latest_job_id</c> at this job. Does not write <c>media_content_id_versions</c> —
    /// that row was inserted earlier by <see cref="ReserveVersionAsync"/>.
    /// </summary>
    Task CommitUploadAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default);

    /// <summary>
    /// Records the <c>.info.json</c> sidecar's storage path / hash / size on the job row.
    /// Does not touch <c>media_content_id_versions</c> or any metadata schema — the sidecar
    /// is a convenience file co-located with the video, not a tracked content version.
    /// </summary>
    Task ApplySidecarUploadCompletedAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default);

    /// <summary>
    /// Records the <c>.meta</c> sidecar's storage path on the job row. The file is
    /// DataBridge-generated and contains title, hash, media GUID, and original URL for
    /// storage-migration correlation.
    /// </summary>
    Task ApplyMetaUploadCompletedAsync(Guid jobId, UploadCompleted evt, CancellationToken ct = default);

    /// <summary>Updates the priority of an existing job. Returns false when the job does not exist.</summary>
    Task<bool> UpdatePriorityAsync(Guid jobId, int priority, CancellationToken ct = default);

    /// <summary>Returns the current state and storage key for a job, or (null, null) when not found.</summary>
    Task<(DownloadJobState? State, string? StorageKey)> GetJobStateAndStorageKeyAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Attempts to move a job into <see cref="DownloadJobState.Cancelling"/>.</summary>
    Task<CancelDownloadDecision> TryBeginCancellationAsync(Guid jobId, string? requestedBy, string? reason, CancellationToken ct = default);

    /// <summary>Marks a cancellation request as the terminal user-visible job result.</summary>
    Task MarkCancelledAsync(Guid jobId, string? message, CancellationToken ct = default);

    /// <summary>Returns all jobs in <see cref="DownloadJobState.DownloadQueued"/> ordered by priority then creation time.</summary>
    Task<IReadOnlyList<DownloadQueuedEntry>> GetDownloadQueuedJobsAsync(CancellationToken ct = default);

    Task IncrementMetadataAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default);

    Task IncrementDownloadAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default);

    Task IncrementUploadAttemptAsync(Guid jobId, int attempt, CancellationToken ct = default);

    Task RecordHistoryAsync(Guid jobId, Guid messageId, string operationKey, string eventName, string? payloadJson, CancellationToken ct = default);

    Task RecordTerminalFailureAsync(Guid jobId, FailureKind kind, string? code, string message, DownloadJobState terminalState, string? lastPayloadJson, CancellationToken ct = default);

    Task ScheduleProviderHaltRetryAsync(Guid jobId, Instant retryAt, CancellationToken ct = default);

    Task MarkProviderHaltRetryDispatchedAsync(Guid jobId, CancellationToken ct = default);

    Task ClearProviderHaltRetryDispatchedAsync(Guid jobId, CancellationToken ct = default);

    Task<IReadOnlyList<ProviderHaltRetryCandidate>> GetDueProviderHaltRetriesAsync(Instant now, CancellationToken ct = default);
}

/// <summary>
/// Result of <see cref="IDownloadJobsRepository.CheckSourceVersionAsync"/>.
/// </summary>
/// <param name="AlreadyDownloaded">
/// True when the source has been downloaded before AND <c>source_last_modified</c> matches
/// AND <c>forceDownload</c> is false. The flow skips download/upload entirely.
/// </param>
/// <param name="MediaGuid">Existing source row's media_guid, when present.</param>
/// <param name="LatestJobId">Existing source row's latest_job_id (for tracing).</param>
public sealed record SourceVersionDecision(
    bool AlreadyDownloaded,
    Guid? MediaGuid,
    Guid? LatestJobId);

/// <summary>
/// Inputs to <see cref="IDownloadJobsRepository.ReserveVersionAsync"/>.
/// </summary>
public sealed record VersionReservationRequest
{
    public required Guid JobId { get; init; }
    public required string ContentHashXxh128 { get; init; }
    public required string StorageKey { get; init; }
    public required string FileName { get; init; }
    public string? Provider { get; init; }
    public string? SourceMediaId { get; init; }
    public Instant? SourceLastModified { get; init; }

    public IngestOrigin IngestOrigin { get; init; } = IngestOrigin.Download;

    public bool LinkSourceToDownloadJob { get; init; } = true;
}

/// <summary>
/// Result of <see cref="IDownloadJobsRepository.ReserveVersionAsync"/>.
/// </summary>
/// <param name="MediaGuid">The media_guid the bytes belong to.</param>
/// <param name="StoragePath">
/// Path the worker should upload to — or, when <see cref="ContentAlreadyStored"/> is true,
/// the path the bytes already live at.
/// </param>
/// <param name="VersionNum">version_num of the content row.</param>
/// <param name="ContentAlreadyStored">
/// True when the (storage_key, content_hash) already existed. Caller should skip the
/// upload and treat the job as a successful no-op duplicate.
/// </param>
/// <param name="IsNewMediaGuid">
/// True when the <c>media_guid</c> was freshly minted by this reservation (neither prior
/// content nor prior source rows existed). Used by the flow to decide whether a full
/// compensating rollback of <c>public.media</c> and <c>media_source_versions</c> is
/// needed if a subsequent step fails.
/// </param>
public sealed record VersionReservation(
    Guid MediaGuid,
    string StoragePath,
    int VersionNum,
    bool ContentAlreadyStored,
    bool IsNewMediaGuid);

/// <summary>A job waiting for a download slot, as returned by <see cref="IDownloadJobsRepository.GetDownloadQueuedJobsAsync"/>.</summary>
public sealed record DownloadQueuedEntry(Guid JobId, int Priority, Instant CreatedAt, string? StorageKey);

public sealed record ProviderHaltRetryCandidate(Guid JobId, Instant RetryAt, DownloadSourceKind SourceKind);

public sealed record CancelDownloadDecision(
    bool Accepted,
    bool Found,
    bool AlreadyTerminal,
    DownloadJobState? State,
    DownloadJobState? PreviousState,
    Guid? CorrelationId,
    string? WorkerTag,
    string? Error)
{
    public static CancelDownloadDecision NotFound()
        => new(false, false, false, null, null, null, null, "Job not found.");

    public static CancelDownloadDecision Rejected(DownloadJobState state, string error, bool alreadyTerminal = false)
        => new(false, true, alreadyTerminal, state, state, null, null, error);

    public static CancelDownloadDecision AcceptedFor(
        Guid correlationId,
        DownloadJobState state,
        DownloadJobState previousState,
        string? workerTag)
        => new(true, true, false, state, previousState, correlationId, workerTag, null);
}
