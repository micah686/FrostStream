using NodaTime;
using Shared.Messaging;

namespace Shared.Database;

public class ImportSessionEntity
{
    public Guid SessionId { get; set; }

    public Guid CorrelationId { get; set; }

    public ImportSessionStatus Status { get; set; }

    public ImportSessionSourceKind SourceKind { get; set; }

    public required string SourceRoot { get; set; }

    public string? SubPath { get; set; }

    public required string StorageKey { get; set; }

    public string? WorkerTag { get; set; }

    public string? RequestedBy { get; set; }

    public int TotalItems { get; set; }

    public int ProbedItems { get; set; }

    public int ReadyItems { get; set; }

    public int IncompleteItems { get; set; }

    public int ExcludedItems { get; set; }

    public int ApprovedItems { get; set; }

    public int ImportedItems { get; set; }

    public int AlreadyImportedItems { get; set; }

    public int FailedItems { get; set; }

    public int MaxParallelItems { get; set; } = 6;

    public string? ErrorMessage { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? CompletedAt { get; set; }
}

public class ImportSessionItemEntity
{
    public Guid ItemId { get; set; }

    public Guid SessionId { get; set; }

    public required string RelativePath { get; set; }

    public required string FileName { get; set; }

    public long FileSizeBytes { get; set; }

    public Instant? FileMtime { get; set; }

    public string? SidecarsJson { get; set; }

    public string? Provider { get; set; }

    public string? SourceMediaId { get; set; }

    public string? SourceUrl { get; set; }

    public string? Title { get; set; }

    public string? ProbeMetadataJson { get; set; }

    public string? ScanMetadataJson { get; set; }

    public string? EnrichedMetadataJson { get; set; }

    public string? UserMetadataJson { get; set; }

    public ImportSessionItemMetadataState MetadataState { get; set; }

    public ImportSessionItemMetadataSource MetadataSource { get; set; }

    public ImportSessionMetadataFetchState MetadataFetchState { get; set; }

    public int MetadataFetchAttempt { get; set; }

    public string? MetadataFetchMessage { get; set; }

    public bool Excluded { get; set; }

    public ImportSessionItemStatus Status { get; set; }

    public int Attempt { get; set; }

    public string? ContentHashXxh128 { get; set; }

    public Guid? MediaGuid { get; set; }

    public string? StoragePath { get; set; }

    public string? StorageVersion { get; set; }

    public string? MetaStoragePath { get; set; }

    public string? InfoJsonStoragePath { get; set; }

    public string? ThumbnailStoragePath { get; set; }

    public string? CaptionStoragePathsJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? CompletedAt { get; set; }
}

public class ImportSessionMappingEntity
{
    public Guid MappingId { get; set; }

    public Guid SessionId { get; set; }

    public required string ObjectBucket { get; set; }

    public required string ObjectKey { get; set; }

    public required string Format { get; set; }

    public int MatchedCount { get; set; }

    public int UnmatchedCount { get; set; }

    public Instant AppliedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}
