using NodaTime;

namespace Shared.Messaging;

public static class OrphanCleanupSubjects
{
    public const string MoveFile = "fs.cleanup.orphans.file.move";
    public const string DeleteFile = "fs.cleanup.orphans.file.delete";
    public const string RestoreFile = "fs.cleanup.orphans.file.restore";
    public const string FileExists = "fs.cleanup.orphans.file.exists";
    public const string AdminList = "fs.cleanup.orphans.admin.list";
    public const string AdminRestoreFile = "fs.cleanup.orphans.admin.file.restore";
    public const string AdminRestoreMetadata = "fs.cleanup.orphans.admin.metadata.restore";
    public const string AdminGetPolicy = "fs.cleanup.orphans.admin.policy.get";
    public const string AdminUpdatePolicy = "fs.cleanup.orphans.admin.policy.update";
}

public sealed record MoveOrphanedFileRequest
{
    public required long OrphanId { get; init; }
    public required string StorageKey { get; init; }
    public required string OriginalStoragePath { get; init; }
    public required string OrphanStoragePath { get; init; }
}

public sealed record MoveOrphanedFileResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record DeleteOrphanedFileRequest
{
    public required long OrphanId { get; init; }
    public required string StorageKey { get; init; }
    public required string OrphanStoragePath { get; init; }
}

public sealed record DeleteOrphanedFileResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record RestoreOrphanedFileRequest
{
    public required long OrphanId { get; init; }
    public required string StorageKey { get; init; }
    public required string OrphanStoragePath { get; init; }
    public required string OriginalStoragePath { get; init; }
}

public sealed record RestoreOrphanedFileResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record OrphanFileExistsRequest
{
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
}

public sealed record OrphanFileExistsResponse
{
    public required bool Success { get; init; }
    public required bool Exists { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record OrphanCleanupListRequest
{
    public string? Kind { get; init; }
    public string? State { get; init; }
    public int PageSize { get; init; } = 100;
    public int Page { get; init; } = 1;
}

public sealed record OrphanCleanupListResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<OrphanCleanupItemDto> Items { get; init; } = [];
}

public sealed record OrphanCleanupItemDto
{
    public required long Id { get; init; }
    public required string Kind { get; init; }
    public required string State { get; init; }
    public required string StorageKey { get; init; }
    public required string OriginalStoragePath { get; init; }
    public string? OrphanStoragePath { get; init; }
    public Guid? MediaGuid { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required DateTimeOffset DeleteAfter { get; init; }
    public DateTimeOffset? MovedAt { get; init; }
    public DateTimeOffset? FinalizedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? LastError { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RestoreOrphanRequest
{
    public required long OrphanId { get; init; }
}

public sealed record RestoreOrphanResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record OrphanCleanupPolicyGetRequest;

public sealed record OrphanCleanupPolicyUpdateRequest
{
    public required bool Enabled { get; init; }
    public required int FileMoveAfterDays { get; init; }
    public required int FilePurgeAfterDays { get; init; }
    public required int MetadataDeleteAfterDays { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record OrphanCleanupPolicyResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public OrphanCleanupPolicyDto? Policy { get; init; }
}

public sealed record OrphanCleanupPolicyDto
{
    public required bool Enabled { get; init; }
    public required int FileMoveAfterDays { get; init; }
    public required int FilePurgeAfterDays { get; init; }
    public required int MetadataDeleteAfterDays { get; init; }
    public string? UpdatedBy { get; init; }
    public Instant? UpdatedAt { get; init; }
    public Instant? LastRunAt { get; init; }
    public int LastMovedCount { get; init; }
    public int LastDeletedFilesCount { get; init; }
    public int LastDeletedMetadataCount { get; init; }
}
