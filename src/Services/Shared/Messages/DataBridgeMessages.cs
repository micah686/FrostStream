using Shared.Entities;

namespace Shared.Messages;

// Requests
public record JobStartRequest(Guid JobId, string IdempotencyKey, string StorageKey, string VideoUrl);
public record JobProgressRequest(Guid JobId, string Status, string? StoragePath, string? FileHash);

/// <summary>
/// Technical media format information for video commit.
/// </summary>
public record MediaFormatInfo(
    long FileSize,
    double? AverageBitRate = null,
    double? AudioBitrate = null,
    double? AudioSamplingRate = null,
    short? AudioChannels = null,
    string? AudioCodec = null,
    int? Width = null,
    int? Height = null,
    string? AspectRatio = null,
    double? VideoBitrate = null,
    float? FrameRate = null,
    string? VideoCodec = null,
    string? DynamicRange = null,
    string? FriendlyVideoResolution = null);

/// <summary>
/// Request to commit a video with versioned storage support.
/// </summary>
public record VideoCommitRequest(
    Guid JobId,
    string IdempotencyKey,
    string StorageKey,
    string StoragePath,
    string FileHash,
    string MetadataJson,
    string Platform,
    DateTime? SourceLastModified,
    MediaType MediaType,
    Quality Quality,
    VideoVariantType VariantType = VideoVariantType.Original,
    Guid? SourceVersionId = null,
    MediaFormatInfo? FormatInfo = null);

public record JobFailRequest(Guid JobId, string ErrorMessage, string? ErrorDetails);
public record JobStatusRequest(Guid JobId);
public record JobLinkCompleteRequest(Guid JobId, Guid ExistingVersionId);

// Responses
public record JobStartResponse(bool Proceed, string? Reason);
public record JobProgressResponse(bool Success, string? ErrorMessage);
public record VideoCommitResponse(bool Success, string? ErrorMessage);
public record JobFailResponse(bool Success);
public record JobPendingLinkInfo(
    Guid SourceJobId,
    string? SourceJobStatus,
    Guid? ExistingVersionId,
    Guid? VideoId,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public record JobStatusResponse(
    Guid JobId,
    string Status,
    string Phase,
    string? SubStatus,
    string? ErrorMessage,
    int RetryCount,
    string? StorageKey,
    string? StoragePath,
    string? FileHash,
    Guid? VideoId,
    DateTime? UpdatedAt,
    DateTime? CompletedAt,
    JobPendingLinkInfo? PendingLink);

/// <summary>
/// Message published to the progress stream for real-time job progress updates.
/// Used by UI/consumers to track download/upload progress.
/// </summary>
public record JobProgressMessage
{
    public Guid JobId { get; init; }
    public string Phase { get; init; } = string.Empty; // "downloading", "uploading", "processing"
    public long BytesProcessed { get; init; }
    public long? TotalBytes { get; init; }
    public int? Percentage { get; init; }
    public Guid WorkerId { get; init; }
    public DateTime Timestamp { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Entry stored in the dead letter queue for permanently failed messages.
/// </summary>
public record DeadLetterEntry
{
    public Guid Id { get; init; }
    public Guid? JobId { get; init; }
    public string OriginalSubject { get; init; } = string.Empty;
    public string? OriginalPayload { get; init; }
    public string FailureReason { get; init; } = string.Empty;
    public DateTime FailedAt { get; init; }
    public int DeliveryAttempts { get; init; }
    public string? CorrelationId { get; init; }
    public string? WorkerId { get; init; }
}

/// <summary>
/// Message published to DLQ stream when a message exhausts all delivery attempts.
/// </summary>
public record DeadLetterMessage
{
    public string OriginalSubject { get; init; } = string.Empty;
    public string? OriginalPayload { get; init; }
    public string LastError { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public DateTime FailedAt { get; init; }
    public string? CorrelationId { get; init; }
    public Guid? JobId { get; init; }
}
