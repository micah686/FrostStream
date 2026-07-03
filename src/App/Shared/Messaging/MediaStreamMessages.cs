namespace Shared.Messaging;

public static class MediaStreamSubjects
{
    public const string Resolve = "media.stream.resolve";
    public const string ResolveThumbnail = "media.thumbnail.resolve";
    public const string ResolveCaption = "media.caption.resolve";
    public const string ProcessorsQueueGroup = "databridge-processors";
}

public sealed record MediaStreamResolveRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public string? StorageKey { get; init; }
    public int? Version { get; init; }
}

public sealed record MediaStreamResolveResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MediaStreamLocationDto? Item { get; init; }
}

public sealed record MediaStreamLocationDto
{
    public required Guid MediaGuid { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
    public required int Version { get; init; }
}

public sealed record MediaThumbnailResolveRequestMessage
{
    public required Guid MediaGuid { get; init; }
}

public sealed record MediaThumbnailResolveResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MediaThumbnailLocationDto? Item { get; init; }
}

public sealed record MediaThumbnailLocationDto
{
    public required Guid MediaGuid { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
}

public sealed record MediaCaptionResolveRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public required string LanguageCode { get; init; }
    public string? CaptionType { get; init; }
}

public sealed record MediaCaptionResolveResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MediaCaptionLocationDto? Item { get; init; }
}

public sealed record MediaCaptionLocationDto
{
    public required Guid MediaGuid { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
    public required string LanguageCode { get; init; }
    public required string CaptionType { get; init; }
}
