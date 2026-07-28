using NodaTime;

namespace Shared.Messaging;

/// <summary>
/// NATS Core request/reply subjects for the durable, job-independent per-media "is this media's audio
/// encoded" fact table (<c>media.audio_encoding_status</c>). Separate from <see cref="AudioRenditionSubjects"/>,
/// which is job/queue-shaped (pending/processing/ready/failed); this surface is meant to remain a
/// stable read/write contract even if rendition or job history is ever purged.
/// </summary>
public static class AudioEncodingStatusSubjects
{
    public const string Set = "media.audio-encoding-status.set";
    public const string SetByMediaGuid = "media.audio-encoding-status.set-by-media-guid";
    public const string ListChannel = "media.audio-encoding-status.channel.list";
    public const string QueueGroup = "databridge-audio-encoding-status";
}

public sealed record SetMediaEncodedStatusRequest
{
    public required long AccountId { get; init; }
    public required Guid MediaGuid { get; init; }
    public required bool IsEncoded { get; init; }
    public string? StorageKey { get; init; }
    public string? StoragePath { get; init; }
}

/// <summary>
/// Same as <see cref="SetMediaEncodedStatusRequest"/>, for callers that only know the media guid, not
/// the owning channel (e.g. a playback endpoint self-healing after discovering a "ready" blob is
/// actually missing). DataBridge resolves the account internally.
/// </summary>
public sealed record SetMediaEncodedStatusByMediaGuidRequest
{
    public required Guid MediaGuid { get; init; }
    public required bool IsEncoded { get; init; }
    public string? StorageKey { get; init; }
    public string? StoragePath { get; init; }
}

public sealed record SetMediaEncodedStatusResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MediaEncodingStatusDto? Item { get; init; }
}

/// <summary>Request for a paged slice of every media item in a channel with its durable encoded status.</summary>
public sealed record ListChannelEncodingStatusRequest
{
    public required long AccountId { get; init; }

    /// <summary>Optional filter: true = only encoded items, false = only not-yet-encoded items, null = all.</summary>
    public bool? IsEncoded { get; init; }

    /// <summary>Optional filter on the media's archived source storage key; null = every storage key.</summary>
    public string? StorageKey { get; init; }

    /// <summary>Requested page size. The read model clamps this to a safe range.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Opaque continuation token returned by a previous page; null for the first page.</summary>
    public string? Cursor { get; init; }
}

public sealed record ListChannelEncodingStatusResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<ChannelEncodedMediaItemDto> Items { get; init; } = [];

    /// <summary>Opaque token to pass as <see cref="ListChannelEncodingStatusRequest.Cursor"/> for the next page; null when exhausted.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total number of media items in the channel matching the filter (across all pages).</summary>
    public int TotalCount { get; init; }

    /// <summary>Total number of encoded media items in the channel, ignoring the filter — for progress %.</summary>
    public int EncodedCount { get; init; }
}

public sealed record ChannelEncodedMediaItemDto
{
    public required Guid MediaGuid { get; init; }
    public required string Title { get; init; }
    public required bool IsEncoded { get; init; }
    public string? StorageKey { get; init; }
    public string? StoragePath { get; init; }
    public Instant? EncodedAt { get; init; }
}

public sealed record MediaEncodingStatusDto
{
    public required Guid MediaGuid { get; init; }
    public required long AccountId { get; init; }
    public required bool IsEncoded { get; init; }
    public string? StorageKey { get; init; }
    public string? StoragePath { get; init; }
    public Instant? EncodedAt { get; init; }
    public Instant UpdatedAt { get; init; }
}
