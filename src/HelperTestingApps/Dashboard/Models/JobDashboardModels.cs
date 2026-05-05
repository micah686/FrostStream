using Shared.Messaging;

namespace Dashboard.Models;

public sealed record JobSummary(
    Guid JobId,
    Guid CorrelationId,
    DownloadJobState State,
    string SourceUrl,
    string? RequestedBy,
    string? StorageKey,
    int AttemptMetadata,
    int AttemptDownload,
    int AttemptUpload,
    long? FileSizeBytes,
    string? ContentHashXxh128,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record JobActivity(
    long Sequence,
    string Subject,
    Guid? JobId,
    Guid? CorrelationId,
    string? OperationKey,
    int? Attempt,
    DateTimeOffset ReceivedAt);

public sealed record DashboardSnapshot(
    IReadOnlyList<JobSummary> Jobs,
    IReadOnlyList<JobActivity> Activity,
    DateTimeOffset LastUpdated,
    bool NatsConnected,
    string? NatsStatus);

