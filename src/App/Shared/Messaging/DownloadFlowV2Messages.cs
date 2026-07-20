using NodaTime;

namespace Shared.Messaging;

/// <summary>The user-visible lifecycle of a download job.</summary>
public enum DownloadJobStatus
{
    Queued = 0,
    Running = 1,
    Stopping = 2,
    Stopped = 3,
    Compensating = 4,
    Completed = 5,
    CompletedWithWarnings = 6,
    Failed = 7,
    AlreadyDownloaded = 8,
    Ignored = 9
}

/// <summary>The current unit of work within a download run.</summary>
public enum DownloadStage
{
    None = 0,
    Metadata = 1,
    DuplicateCheck = 2,
    WaitingForWorker = 3,
    MediaAcquire = 4,
    PrimaryMediaUpload = 5,
    MetaSidecarUpload = 6,
    InfoJsonUpload = 7,
    ThumbnailUpload = 8,
    CaptionUpload = 9,
    RichMetadataWrite = 10,
    Finalize = 11,
    Cleanup = 12,
    Compensation = 13
}

/// <summary>The execution state of a stage, separate from the job lifecycle.</summary>
public enum DownloadStageStatus
{
    Pending = 0,
    Running = 1,
    RetryWaiting = 2,
    Succeeded = 3,
    Skipped = 4,
    Warning = 5,
    Failed = 6,
    Stopped = 7
}

public enum DownloadGroupKind
{
    Direct = 0,
    Playlist = 1,
    Channel = 2,
    CreatorMonitor = 3
}

public enum DownloadGroupStatus
{
    Queued = 0,
    Expanding = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Completed = 5,
    CompletedWithWarnings = 6,
    CompletedWithFailures = 7,
    Failed = 8
}

public enum DownloadArtifactStatus
{
    Pending = 0,
    Uploading = 1,
    Stored = 2,
    Warning = 3,
    Failed = 4,
    Deleted = 5,
    Residual = 6
}

public enum DownloadWorkerLeaseStatus
{
    Active = 0,
    Released = 1,
    Expired = 2,
    Rejected = 3,
    Stopped = 4
}

/// <summary>
/// Immutable execution identity carried by every V2 Worker command and result. A message is
/// actionable only while all values match the job's current run and stage dispatch.
/// </summary>
public sealed record DownloadExecutionIdentity
{
    public required Guid JobId { get; init; }
    public required Guid RunId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid DispatchId { get; init; }
    public required DownloadStage Stage { get; init; }
    public string? ArtifactKey { get; init; }
    public required int Attempt { get; init; }
}

/// <summary>Parameter persisted in a Cleipnir job-flow instance.</summary>
public sealed record DownloadRunRequest
{
    public required Guid RunId { get; init; }
    public required int RunNumber { get; init; }
    public required DownloadRequested Request { get; init; }
}

/// <summary>Root request for the unified direct/playlist/channel group flow.</summary>
public sealed record DownloadGroupRequested
{
    public required Guid GroupId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required DownloadGroupKind Kind { get; init; }
    public required string SourceUrl { get; init; }
    public string? RequestedBy { get; init; }
    public string? StorageKey { get; init; }
    public int Priority { get; init; }
    public DownloadRequested? DirectRequest { get; init; }
    public PlaylistRequested? CollectionRequest { get; init; }
    public ChannelMediaListRequested? ChannelRequest { get; init; }
}

public sealed record DownloadGroupExpansionSucceeded
{
    public required Guid GroupId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid MessageId { get; init; }
    public required Guid CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public int ExpectedJobs { get; init; }
}

public sealed record DownloadGroupExpansionFailed
{
    public required Guid GroupId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid MessageId { get; init; }
    public required Guid CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record StartDownloadRequest
{
    public required Guid JobId { get; init; }
    public string? RequestedBy { get; init; }
}

public sealed record StartDownloadResponse
{
    public bool Success { get; init; }
    public Guid? JobId { get; init; }
    public Guid? RunId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StopDownloadRequest
{
    public required Guid JobId { get; init; }
    public string? RequestedBy { get; init; }
    public string? Reason { get; init; }
}

public sealed record StopDownloadResponse
{
    public bool Success { get; init; }
    public DownloadJobStatus? Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StartDownloadGroupRequest
{
    public required Guid CorrelationId { get; init; }
    public string? RequestedBy { get; init; }
}

public sealed record StopDownloadGroupRequest
{
    public required Guid CorrelationId { get; init; }
    public string? RequestedBy { get; init; }
    public string? Reason { get; init; }
}

public sealed record DownloadGroupControlResponse
{
    public bool Success { get; init; }
    public int AffectedJobs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record ClearProviderCircuitRequest
{
    public required string Provider { get; init; }
    public string? RequestedBy { get; init; }
}

public sealed record ClearProviderCircuitResponse
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record AcquireDownloadLeaseRequest
{
    public required DownloadExecutionIdentity Execution { get; init; }
    public required string WorkerInstanceId { get; init; }
    public required Instant OccurredAt { get; init; }
}

public sealed record AcquireDownloadLeaseResponse
{
    public bool Granted { get; init; }
    /// <summary>
    /// The dispatch was already published when the user requested Stop. The Worker must claim
    /// and immediately cancel it so the immutable flow receives a terminal stage result instead
    /// of waiting forever for a command that was merely acknowledged as stale.
    /// </summary>
    public bool StopRequested { get; init; }
    public Instant? ExpiresAt { get; init; }
    public string? RejectionCode { get; init; }
}

public sealed record RenewDownloadLeaseRequest
{
    public required Guid DispatchId { get; init; }
    public required Guid RunId { get; init; }
    public required string WorkerInstanceId { get; init; }
    public required Instant OccurredAt { get; init; }
}

public sealed record RenewDownloadLeaseResponse
{
    public bool Renewed { get; init; }
    public Instant? ExpiresAt { get; init; }
}

public sealed record StopActiveDownloadRun
{
    public required Guid JobId { get; init; }
    public required Guid RunId { get; init; }
    public Guid? DispatchId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Durable message sent to the exact Cleipnir run when a user stops it.</summary>
public sealed record DownloadRunStopRequested
{
    public required Guid JobId { get; init; }
    public required Guid RunId { get; init; }
    public required Guid MessageId { get; init; }
    public string? Reason { get; init; }
    public required Instant OccurredAt { get; init; }
}

/// <summary>Stops collection expansion in the exact durable group flow.</summary>
public sealed record DownloadGroupStopRequested
{
    public required Guid GroupId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid MessageId { get; init; }
    public string? Reason { get; init; }
    public required Instant OccurredAt { get; init; }
}

public static class DownloadFlowInstance
{
    public static string Job(Guid jobId, Guid runId) => $"{jobId:N}-{runId:N}";
    public static string Group(Guid groupId) => groupId.ToString("N");
}

public interface IDownloadStageEvent
{
    DownloadExecutionIdentity Execution { get; init; }
    Guid MessageId { get; init; }
    Guid? CausationId { get; init; }
    string OperationKey { get; init; }
    Instant OccurredAt { get; init; }
    string WorkerInstanceId { get; init; }
}

public sealed record DownloadStageStarted : IDownloadStageEvent
{
    public required DownloadExecutionIdentity Execution { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required string WorkerInstanceId { get; init; }
}

public sealed record DownloadStageHeartbeat : IDownloadStageEvent
{
    public required DownloadExecutionIdentity Execution { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required string WorkerInstanceId { get; init; }
}

public sealed record DownloadStageSucceeded : IDownloadStageEvent
{
    public required DownloadExecutionIdentity Execution { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required string WorkerInstanceId { get; init; }
}

public sealed record DownloadStageFailed : IDownloadStageEvent
{
    public required DownloadExecutionIdentity Execution { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required string WorkerInstanceId { get; init; }
    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record DownloadStageStopped : IDownloadStageEvent
{
    public required DownloadExecutionIdentity Execution { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? CausationId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required string WorkerInstanceId { get; init; }
    public string? Reason { get; init; }
}
