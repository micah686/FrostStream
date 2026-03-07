namespace Shared.Messages;

// Requests
public record JobStartRequest(Guid JobId, string IdempotencyKey, string StorageKey, string VideoUrl);
public record JobProgressRequest(Guid JobId, string Status, string? StoragePath, string? FileHash);
public record VideoCommitRequest(Guid JobId, string IdempotencyKey, string StorageKey, string StoragePath, string FileHash, string MetadataJson, string Platform, DateTime? SourceLastModified);
public record JobFailRequest(Guid JobId, string ErrorMessage, string? ErrorDetails);
public record JobStatusRequest(Guid JobId);
public record JobLinkCompleteRequest(Guid JobId, Guid ExistingVersionId);

// Responses
public record JobStartResponse(bool Proceed, string? Reason);
public record JobProgressResponse(bool Success, string? ErrorMessage);
public record VideoCommitResponse(bool Success, string? ErrorMessage);
public record JobFailResponse(bool Success);
public record JobStatusResponse(string Status, string? ErrorMessage, int RetryCount, string? StorageKey);
