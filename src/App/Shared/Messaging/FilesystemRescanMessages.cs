namespace Shared.Messaging;

public sealed record FilesystemRescanStorageKeysRequest;

public sealed record FilesystemRescanStorageKeysResponse
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> StorageKeys { get; init; } = [];
}

public sealed record FilesystemRescanReconcileRequest
{
    public required string ScheduleKey { get; init; }
    public required string StorageKey { get; init; }
    public required string ObjectBucket { get; init; }
    public required string ObjectKey { get; init; }
}

public sealed record FilesystemRescanReconcileResponse
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int MissingCount { get; init; }
    public int UnexpectedCount { get; init; }
}
