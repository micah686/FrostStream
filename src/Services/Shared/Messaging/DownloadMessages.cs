using NodaTime;

namespace Shared.Messaging;

public interface IFlowMessage
{
    Guid JobId { get; init; }
    Guid CorrelationId { get; init; }
    Guid? CausationId { get; init; }
    Guid MessageId { get; init; }
    string OperationKey { get; init; }
    Instant OccurredAt { get; init; }
    int Attempt { get; init; }
}

public enum DownloadJobState
{
    Queued = 0,
    MetadataPending = 1,
    MetadataResolved = 2,
    DownloadPending = 3,
    DownloadedTemp = 4,
    UploadPending = 5,
    Uploaded = 6,
    CommitPending = 7,
    Completed = 8,
    Compensating = 9,
    FailedTransient = 10,
    FailedPermanent = 11,
    DeadLettered = 12
}

public enum FailureKind
{
    Unknown = 0,
    Transient = 1,
    Permanent = 2,
    Timeout = 3,
    Cancelled = 4
}

public sealed record DownloadRequested : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    public required string SourceUrl { get; init; }
    public string? RequestedBy { get; init; }
    public string? StorageKey { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record FetchMetadataCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string SourceUrl { get; init; }
}

public sealed record MetadataFetched : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public string? SourceVideoId { get; init; }
    public string? Provider { get; init; }
    public string? Title { get; init; }
    public string? Uploader { get; init; }
    public required string ArchiveKey { get; init; }
}

public sealed record MetadataFetchFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record DownloadVideoCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string SourceUrl { get; init; }
    public required string ArchiveKey { get; init; }
}

public sealed record DownloadCompleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string TempFileRef { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public string? ContentHashXxh128 { get; init; }
    public string? ContentType { get; init; }
}

public sealed record DownloadFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
    public string? TempFileRef { get; init; }
}

public sealed record UploadObjectCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string TempFileRef { get; init; }
    public required string StorageKey { get; init; }
    public required string ArchiveKey { get; init; }
}

public sealed record UploadCompleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string TempFileRef { get; init; }
    public required string StorageKey { get; init; }
    public required string ObjectKey { get; init; }
    public string? StorageVersion { get; init; }
    public string? ContentHashXxh128 { get; init; }
    public long? ContentLengthBytes { get; init; }
}

public sealed record UploadFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
    public string? TempFileRef { get; init; }
}

public sealed record DeleteTempFileCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string TempFileRef { get; init; }
}

public sealed record TempFileDeleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string TempFileRef { get; init; }
}

public sealed record TempFileDeleteFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string TempFileRef { get; init; }
    public required FailureKind FailureKind { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record DeleteUploadedObjectCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string StorageKey { get; init; }
    public required string ObjectKey { get; init; }
    public string? StorageVersion { get; init; }
}

public sealed record UploadedObjectDeleted : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string StorageKey { get; init; }
    public required string ObjectKey { get; init; }
    public string? StorageVersion { get; init; }
}

public sealed record UploadedObjectDeleteFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string StorageKey { get; init; }
    public required string ObjectKey { get; init; }
    public string? StorageVersion { get; init; }
    public required FailureKind FailureKind { get; init; }
    public required string ErrorMessage { get; init; }
}
