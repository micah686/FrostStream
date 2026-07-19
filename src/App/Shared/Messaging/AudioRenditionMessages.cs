using NodaTime;

namespace Shared.Messaging;

public enum AudioRenditionStatus
{
    Pending = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3
}

public static class AudioRenditionSubjects
{
    public const string Resolve = "media.audio-rendition.resolve";
    public const string ResolveChannel = "media.audio-rendition.channel.resolve";
    public const string Claim = "media.audio-rendition.claim";
    public const string Complete = "media.audio-rendition.complete";
    public const string Fail = "media.audio-rendition.fail";
    public const string ProcessorsQueueGroup = "databridge-audio-renditions";
}

public sealed record ChannelAudioResolveRequest
{
    public required long AccountId { get; init; }
    public bool CreateIfMissing { get; init; }
    public bool RetryFailedAndPending { get; init; }
}

public sealed record ChannelAudioResolveResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public ChannelAudioDto? Item { get; init; }
}

public sealed record ChannelAudioDto
{
    public required long AccountId { get; init; }
    public required string AccountName { get; init; }
    public string? AccountDescription { get; init; }
    public string? AvatarStoragePath { get; init; }
    public int TotalCount { get; init; }
    public int MissingCount { get; init; }
    public int PendingCount { get; init; }
    public int ProcessingCount { get; init; }
    public int ReadyCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<ChannelAudioItemDto> Items { get; init; } = [];
}

public sealed record ChannelAudioItemDto
{
    public required Guid MediaGuid { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public Instant? ReleaseDate { get; init; }
    public int? DurationSeconds { get; init; }
    public AudioRenditionDto? Rendition { get; init; }
}

public sealed record AudioRenditionEncodeRequested
{
    public required Guid RenditionId { get; init; }
    public required Guid MediaGuid { get; init; }
    public required int SourceVersion { get; init; }
}

public sealed record AudioRenditionResolveRequest
{
    public required Guid MediaGuid { get; init; }
    public string? StorageKey { get; init; }
    public int? SourceVersion { get; init; }
    public bool CreateIfMissing { get; init; }
}

public sealed record AudioRenditionResolveResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public AudioRenditionDto? Item { get; init; }
}

public sealed record AudioRenditionClaimRequest
{
    public required Guid RenditionId { get; init; }
}

public sealed record AudioRenditionClaimResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public AudioRenditionWorkItem? Item { get; init; }
}

public sealed record AudioRenditionCompleteRequest
{
    public required Guid RenditionId { get; init; }
    public required string StoragePath { get; init; }
    public required string ContentHashXxh128 { get; init; }
    public required long SizeBytes { get; init; }
    public int? DurationSeconds { get; init; }
}

public sealed record AudioRenditionCompleteResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record AudioRenditionFailRequest
{
    public required Guid RenditionId { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record AudioRenditionFailResponse
{
    public bool Success { get; init; }
}

public sealed record AudioRenditionDto
{
    public required Guid RenditionId { get; init; }
    public required Guid MediaGuid { get; init; }
    public required int SourceVersion { get; init; }
    public required AudioRenditionStatus Status { get; init; }
    public required string StorageKey { get; init; }
    public string? StoragePath { get; init; }
    public long? SizeBytes { get; init; }
    public int? DurationSeconds { get; init; }
    public string? ErrorMessage { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
}

public sealed record AudioRenditionWorkItem
{
    public required Guid RenditionId { get; init; }
    public required Guid MediaGuid { get; init; }
    public required int SourceVersion { get; init; }
    public required string SourceStorageKey { get; init; }
    public required string SourceStoragePath { get; init; }
    public required string OutputStorageKey { get; init; }
    public required string OutputStoragePath { get; init; }
}
