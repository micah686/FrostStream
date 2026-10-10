using NodaTime;

namespace Shared.Messaging;

public enum StreamRenditionStatus
{
    Pending = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3
}

public static class StreamRenditionSubjects
{
    public const string Resolve = "media.stream-rendition.resolve";
    public const string Claim = "media.stream-rendition.claim";
    public const string Complete = "media.stream-rendition.complete";
    public const string Fail = "media.stream-rendition.fail";
    public const string ProcessorsQueueGroup = "databridge-stream-renditions";
}

/// <summary>
/// JetStream job asking MediaProcessor to produce the stream/casting HLS rendition
/// (H.264 + AAC segments) for one stored media version.
/// </summary>
public sealed record StreamRenditionEncodeRequested
{
    public required Guid RenditionId { get; init; }
    public required Guid MediaGuid { get; init; }
    public required int SourceVersion { get; init; }
}

public sealed record StreamRenditionResolveRequest
{
    public required Guid MediaGuid { get; init; }
    public string? StorageKey { get; init; }
    public int? SourceVersion { get; init; }
    public bool CreateIfMissing { get; init; }
}

public sealed record StreamRenditionResolveResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public StreamRenditionDto? Item { get; init; }
}

public sealed record StreamRenditionClaimRequest
{
    public required Guid RenditionId { get; init; }
}

public sealed record StreamRenditionClaimResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public StreamRenditionWorkItem? Item { get; init; }
}

public sealed record StreamRenditionCompleteRequest
{
    public required Guid RenditionId { get; init; }

    /// <summary>Storage directory the HLS assets were written to (manifest is index.m3u8 inside it).</summary>
    public required string StoragePath { get; init; }

    public required long SizeBytes { get; init; }
    public int? DurationSeconds { get; init; }
}

public sealed record StreamRenditionCompleteResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StreamRenditionFailRequest
{
    public required Guid RenditionId { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record StreamRenditionFailResponse
{
    public bool Success { get; init; }
}

public sealed record StreamRenditionDto
{
    public required Guid RenditionId { get; init; }
    public required Guid MediaGuid { get; init; }
    public required int SourceVersion { get; init; }
    public required StreamRenditionStatus Status { get; init; }
    public required string StorageKey { get; init; }

    /// <summary>Storage directory holding index.m3u8 and its segments once the rendition is ready.</summary>
    public string? StoragePath { get; init; }

    public long? SizeBytes { get; init; }
    public int? DurationSeconds { get; init; }
    public string? ErrorMessage { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
}

public sealed record StreamRenditionWorkItem
{
    public required Guid RenditionId { get; init; }
    public required Guid MediaGuid { get; init; }
    public required int SourceVersion { get; init; }
    public required string SourceStorageKey { get; init; }
    public required string SourceStoragePath { get; init; }
    public required string OutputStorageKey { get; init; }
    public required string OutputStoragePath { get; init; }
}
