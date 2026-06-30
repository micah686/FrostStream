using NodaTime;
using Shared.Messaging;

namespace Shared.Database;

public class LocalImportBatchEntity
{
    public Guid BatchId { get; set; }

    public Guid CorrelationId { get; set; }

    public LocalImportStatus Status { get; set; }

    public required string ManifestObjectBucket { get; set; }

    public required string ManifestObjectKey { get; set; }

    public required string SourceRoot { get; set; }

    public required string StorageKey { get; set; }

    public string? RequestedBy { get; set; }

    public string? RequestedByContext { get; set; }

    public int TotalItems { get; set; }

    public int CompletedItems { get; set; }

    public int AlreadyImportedItems { get; set; }

    public int FailedItems { get; set; }

    public string? ErrorMessage { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? CompletedAt { get; set; }
}

public class LocalImportItemEntity
{
    public Guid ItemId { get; set; }

    public Guid BatchId { get; set; }

    public int ItemIndex { get; set; }

    public LocalImportStatus Status { get; set; }

    public required string SourceRoot { get; set; }

    public required string RelativePath { get; set; }

    public required string StorageKey { get; set; }

    public string? Provider { get; set; }

    public string? SourceMediaId { get; set; }

    public Instant? SourceLastModified { get; set; }

    public string? SourceUrl { get; set; }

    public string? Title { get; set; }

    public long? FileSizeBytes { get; set; }

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

public enum IngestOrigin
{
    Download = 0,
    LocalImport = 1
}
