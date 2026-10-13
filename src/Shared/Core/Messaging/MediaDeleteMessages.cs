namespace Shared.Messaging;

/// <summary>
/// Subjects for user-initiated video deletion (WebAPI -> DataBridge, request/reply).
/// </summary>
public static class MediaDeleteSubjects
{
    /// <summary>Delete a video globally: every storage copy, its metadata, and search entries.</summary>
    public const string Delete = "media.delete";

    /// <summary>
    /// Delete a video's copy on a single storage key. When that key holds the last remaining
    /// copy, the operation cascades to a full (global) delete.
    /// </summary>
    public const string DeleteForStorageKey = "media.delete-for-key";
}

/// <summary>Subjects for physical storage file deletion (DataBridge -> Worker, request/reply).</summary>
public static class MediaFileSubjects
{
    public const string Delete = "media.file.delete";
}

public sealed record MediaDeleteRequest
{
    public required Guid MediaGuid { get; init; }
}

public sealed record MediaDeleteForStorageKeyRequest
{
    public required Guid MediaGuid { get; init; }
    public required string StorageKey { get; init; }
}

public sealed record MediaDeleteResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Number of physical storage objects deleted across all affected storage keys.</summary>
    public int FilesDeleted { get; init; }

    /// <summary>True when the media record itself (metadata + search) was removed.</summary>
    public bool MediaRemoved { get; init; }
}

public sealed record DeleteMediaFileRequest
{
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
}

public sealed record DeleteMediaFileResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
