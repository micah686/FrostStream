namespace Shared.Messaging;

public static class MediaContentSubjects
{
    public const string Resolve = "media.content.resolve";
    public const string ProcessorsQueueGroup = "databridge-processors";
}

public sealed record MediaContentResolveRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public string? StorageKey { get; init; }
    public int? Version { get; init; }
}

public sealed record MediaContentResolveResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MediaContentLocationDto? Item { get; init; }
}

public sealed record MediaContentLocationDto
{
    public required Guid MediaGuid { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
    public required int Version { get; init; }
}
