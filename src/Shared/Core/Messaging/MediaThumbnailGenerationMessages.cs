namespace Shared.Messaging;

public static class MediaThumbnailGenerationSubjects
{
    public const string ListMissing = "media.thumbnail-generation.list-missing";
    public const string Complete = "media.thumbnail-generation.complete";
    public const string QueueGroup = "databridge-thumbnail-generation";
}

public sealed record GenerateMissingMediaThumbnailsRequested : ScheduledBackgroundRequest
{
    public required long AccountId { get; init; }
}

public sealed record MissingMediaThumbnailsRequest
{
    public required long AccountId { get; init; }
    public Guid? AfterMediaGuid { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record MissingMediaThumbnailsResponse
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<MissingMediaThumbnailItem> Items { get; init; } = [];
}

public sealed record MissingMediaThumbnailItem
{
    public required Guid MediaGuid { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
}

public sealed record MediaThumbnailGeneratedRequest
{
    public required Guid MediaGuid { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
}

public sealed record MediaThumbnailGeneratedResponse
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
