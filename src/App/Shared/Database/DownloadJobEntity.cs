using NodaTime;
using Shared.Messaging;

namespace Shared.Database;

public class DownloadJobEntity
{
    public Guid JobId { get; set; }

    public Guid CorrelationId { get; set; }

    public DownloadJobState State { get; set; }

    public required string SourceUrl { get; set; }

    public string? RequestedBy { get; set; }

    public string? StorageKey { get; set; }

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

    public IngestOrigin IngestOrigin { get; set; } = IngestOrigin.Download;

    public FailureKind? FailureKind { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? CompletedAt { get; set; }
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

    public AudioRenditionFormat Format { get; set; }

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
