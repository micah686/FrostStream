using NodaTime;
using Shared.Messaging;

namespace Shared.Database;

public class DownloadJobEntity
{
    public Guid JobId { get; set; }

    public Guid CorrelationId { get; set; }

    public DownloadJobState State { get; set; }

    /// <summary>Authoritative V2 lifecycle. <see cref="State"/> remains only for legacy-schema compatibility.</summary>
    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;

    public DownloadStage Stage { get; set; } = DownloadStage.None;

    public DownloadStageStatus StageStatus { get; set; } = DownloadStageStatus.Pending;

    public Guid? CurrentRunId { get; set; }

    public int CurrentRunNumber { get; set; }

    public int CurrentAttempt { get; set; }

    public string? CurrentArtifactKey { get; set; }

    public int WarningCount { get; set; }

    public Instant? StopRequestedAt { get; set; }

    public string? StopRequestedBy { get; set; }

    public string? StopReason { get; set; }

    public required string SourceUrl { get; set; }

    public string? RequestedBy { get; set; }

    public string? StorageKey { get; set; }

    /// <summary>When <see cref="State"/> is <see cref="DownloadJobState.Ignored"/>, the config-set
    /// keyword pattern that suppressed this entry (for reporting). Null otherwise.</summary>
    public string? IgnoredKeyword { get; set; }

    public int AttemptMetadata { get; set; }

    public int AttemptDownload { get; set; }

    public int AttemptUpload { get; set; }

    public string? TempFileRef { get; set; }

    public long? FileSizeBytes { get; set; }

    public string? ContentHashXxh128 { get; set; }

    public string? StorageVersion { get; set; }

    public string? InfoJsonStoragePath { get; set; }

    public string? InfoJsonContentHashXxh128 { get; set; }

    public long? InfoJsonSizeBytes { get; set; }

    public string? MetaStoragePath { get; set; }

    public int Priority { get; set; }

    public DownloadSourceKind SourceKind { get; set; } = DownloadSourceKind.Direct;

    public IngestOrigin IngestOrigin { get; set; } = IngestOrigin.Download;

    public FailureKind? FailureKind { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? CompletedAt { get; set; }
}

public class DownloadGroupEntity
{
    public Guid GroupId { get; set; }
    public Guid CorrelationId { get; set; }
    public DownloadGroupKind Kind { get; set; }
    public DownloadGroupStatus Status { get; set; }
    public required string SourceUrl { get; set; }
    public string? RequestedBy { get; set; }
    public string? StorageKey { get; set; }
    public int TotalJobs { get; set; }
    public int CompletedJobs { get; set; }
    public int WarningJobs { get; set; }
    public int FailedJobs { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
    public Instant? CompletedAt { get; set; }
}

public class DownloadJobRunEntity
{
    public Guid RunId { get; set; }
    public Guid JobId { get; set; }
    public int RunNumber { get; set; }
    public DownloadJobStatus Status { get; set; }
    public DownloadStage Stage { get; set; }
    public DownloadStageStatus StageStatus { get; set; }
    public FailureKind? FailureKind { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
    public Instant? StartedAt { get; set; }
    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
    public Instant? EndedAt { get; set; }
}

public class DownloadStageAttemptEntity
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid JobId { get; set; }
    public DownloadStage Stage { get; set; }
    public string ArtifactKey { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public DownloadStageStatus Status { get; set; }
    public Guid DispatchId { get; set; }
    public required string OperationKey { get; set; }
    public FailureKind? FailureKind { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
    public Instant? StartedAt { get; set; }
    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
    public Instant? EndedAt { get; set; }
}

public class DownloadArtifactEntity
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid JobId { get; set; }
    public DownloadStage Stage { get; set; }
    public required string ArtifactKey { get; set; }
    public UploadArtifactKind Kind { get; set; }
    public bool Required { get; set; }
    public DownloadArtifactStatus Status { get; set; }
    public string? TempFileRef { get; set; }
    public string? StorageKey { get; set; }
    public string? StoragePath { get; set; }
    public string? StorageVersion { get; set; }
    public string? ContentHashXxh128 { get; set; }
    public long? SizeBytes { get; set; }
    public string? WarningCode { get; set; }
    public string? WarningMessage { get; set; }
    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
}

public class DownloadWorkerLeaseEntity
{
    public Guid DispatchId { get; set; }
    public Guid RunId { get; set; }
    public Guid JobId { get; set; }
    public DownloadStage Stage { get; set; }
    public string ArtifactKey { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public required string WorkerInstanceId { get; set; }
    public DownloadWorkerLeaseStatus Status { get; set; }
    public Instant AcquiredAt { get; set; }
    public Instant LastHeartbeatAt { get; set; }
    public Instant ExpiresAt { get; set; }
    public Instant? ReleasedAt { get; set; }
}

public class DownloadJobWarningEntity
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid JobId { get; set; }
    public DownloadStage Stage { get; set; }
    public string ArtifactKey { get; set; } = string.Empty;
    public required string WarningCode { get; set; }
    public required string WarningMessage { get; set; }
    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}

public class DownloadJobHistoryEntity
{
    public long Id { get; set; }

    public Guid JobId { get; set; }

    public Guid MessageId { get; set; }

    public required string OperationKey { get; set; }

    public required string EventName { get; set; }

    public string? PayloadJson { get; set; }

    public Instant RecordedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}

/// <summary>
/// One persisted advisory yt-dlp progress line for a job (durable version of the live-only
/// <see cref="Shared.Messaging.DownloadProgress"/> broadcast), so the job log survives a page refresh.
/// </summary>
public class DownloadJobProgressLogEntity
{
    public long Id { get; set; }

    public Guid JobId { get; set; }

    public int Sequence { get; set; }

    public required string Message { get; set; }

    public Instant RecordedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}

public class FailedDownloadJobEntity
{
    public Guid JobId { get; set; }

    public Guid CorrelationId { get; set; }

    public DownloadJobState FailedState { get; set; }

    public FailureKind FailureKind { get; set; }

    public string? FailureCode { get; set; }

    public required string FailureMessage { get; set; }

    public string? LastPayloadJson { get; set; }

    public Instant FailedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}

public class ProcessedMessageEntity
{
    public Guid MessageId { get; set; }

    public required string OperationKey { get; set; }

    public Guid JobId { get; set; }

    public Instant ProcessedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}

public class MediaEntity
{
    public Guid MediaGuid { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}

public class MediaSourceVersionEntity
{
    public long Id { get; set; }

    public string? Provider { get; set; }

    public string? SourceMediaId { get; set; }

    public Instant? SourceLastModified { get; set; }

    public Guid MediaGuid { get; set; }

    public Guid? LatestJobId { get; set; }
}

public class MediaContentIdVersionEntity
{
    public long Id { get; set; }

    public Guid MediaGuid { get; set; }

    public required string ContentHashXxh128 { get; set; }

    public required string StorageKey { get; set; }

    public required string StoragePath { get; set; }

    public int VersionNum { get; set; }

    public IngestOrigin IngestOrigin { get; set; } = IngestOrigin.Download;
}

public class AudioRenditionEntity
{
    public Guid RenditionId { get; set; }

    public Guid MediaGuid { get; set; }

    public int SourceVersionNum { get; set; }

    public AudioRenditionStatus Status { get; set; }

    public required string StorageKey { get; set; }

    public string? StoragePath { get; set; }

    public string? ContentHashXxh128 { get; set; }

    public long? SizeBytes { get; set; }

    public int? DurationSeconds { get; set; }

    public string? ErrorMessage { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
}

public class StreamRenditionEntity
{
    public Guid RenditionId { get; set; }

    public Guid MediaGuid { get; set; }

    public int SourceVersionNum { get; set; }

    public StreamRenditionStatus Status { get; set; }

    public required string StorageKey { get; set; }

    /// <summary>Storage directory holding the HLS manifest (index.m3u8) and its segments.</summary>
    public string? StoragePath { get; set; }

    public long? SizeBytes { get; set; }

    public int? DurationSeconds { get; set; }

    public string? ErrorMessage { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
}
