using NodaTime;
using Shared.Imports;

namespace Shared.Messaging;

public enum LocalImportStatus
{
    Queued = 0,
    Preparing = 1,
    Uploading = 2,
    Completed = 3,
    AlreadyImported = 4,
    Failed = 5
}

public sealed record LocalMediaImportRequested : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    public Guid BatchId => JobId;

    public required string StorageKey { get; init; }

    /// <summary>
    /// Worker tag the import should run on. When null/empty the flow falls back to the tag
    /// configured on the destination storage target (or any worker if that is also unset).
    /// </summary>
    public string? WorkerTag { get; init; }

    public string? RequestedBy { get; init; }

    public string? RequestedByContext { get; init; }
}

public sealed record ReadLocalImportManifestCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid BatchId { get; init; }

    public string? RequiredWorkerTag { get; init; }
}

public sealed record LocalImportManifestRead : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid BatchId { get; init; }

    public required LocalMediaImportManifest Manifest { get; init; }
}

public sealed record LocalImportManifestReadFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid BatchId { get; init; }

    public string? ErrorCode { get; init; }

    public required string ErrorMessage { get; init; }
}

public sealed record PrepareLocalImportFileCommand : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid BatchId { get; init; }

    public required Guid ItemId { get; init; }

    public required string File { get; init; }

    public LocalMediaImportManifestSidecars? Sidecars { get; init; }

    public string? RequiredWorkerTag { get; init; }
}

public sealed record LocalImportFilePrepared : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid BatchId { get; init; }

    public required Guid ItemId { get; init; }

    public required string SourceFileRef { get; init; }

    public required string FileName { get; init; }

    public long FileSizeBytes { get; init; }

    public required string ContentHashXxh128 { get; init; }

    public LocalImportPreparedSidecar? InfoJson { get; init; }

    public LocalImportPreparedSidecar? Thumbnail { get; init; }

    public IReadOnlyList<LocalImportPreparedCaptionSidecar> Captions { get; init; } = [];
}

public record LocalImportPreparedSidecar
{
    public required string SourceFileRef { get; init; }

    public required string FileName { get; init; }

    public long SizeBytes { get; init; }

    public required string ContentHashXxh128 { get; init; }
}

public sealed record LocalImportPreparedCaptionSidecar : LocalImportPreparedSidecar
{
    public string? LanguageCode { get; init; }

    public string? CaptionType { get; init; }

    public string? Name { get; init; }
}

public sealed record LocalImportFilePrepareFailed : IFlowMessage
{
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required Guid BatchId { get; init; }

    public required Guid ItemId { get; init; }

    public required FailureKind FailureKind { get; init; }

    public string? ErrorCode { get; init; }

    public required string ErrorMessage { get; init; }
}
