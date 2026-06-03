namespace Shared.Messaging;

public static class OrphanCleanupSubjects
{
    public const string MoveFile = "fs.cleanup.orphans.file.move";
    public const string DeleteFile = "fs.cleanup.orphans.file.delete";
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
    public string? ErrorMessage { get; init; }
}
