using NodaTime;

namespace Shared.Messaging;

/// <summary>
/// Lifecycle states for a playlist record.
/// Numeric values are part of the wire/database contract — never renumber existing entries.
/// </summary>
public enum PlaylistState
{
    /// <summary>Submitted; waiting for yt-dlp to resolve the entry list.</summary>
    PendingMetadata = 0,
    /// <summary>Entry list fetched; per-item fan-out is in progress or complete.</summary>
    MetadataResolved = 1,
    /// <summary>Metadata fetch failed.</summary>
    Failed = 2
}

/// <summary>
/// Common envelope for playlist-pipeline JetStream messages. Mirrors
/// <see cref="IFlowMessage"/> but is keyed by <see cref="PlaylistId"/> rather than a per-job id,
/// since a playlist fans out into many download jobs.
/// </summary>
public interface IPlaylistFlowMessage
{
    Guid PlaylistId { get; init; }
    Guid CorrelationId { get; init; }
    Guid? CausationId { get; init; }
    Guid MessageId { get; init; }
    string OperationKey { get; init; }
    Instant OccurredAt { get; init; }
    int Attempt { get; init; }
}

public static class FetchPlaylistMetadataCommandDefaults
{
    public const int PageStartIndex = 1;
    public const int PageSize = 5_000;
}

/// <summary>
/// Root event for the playlist pipeline. Published by WebAPI when a user submits a playlist URL.
/// DataBridge persists/reuses the playlist row and emits <see cref="FetchPlaylistMetadataCommand"/>.
/// </summary>
public sealed record PlaylistRequested : IPlaylistFlowMessage
{
    public required Guid PlaylistId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public int Attempt { get; init; } = 1;

    /// <summary>Submitted playlist URL. Whatever yt-dlp accepts as a playlist source.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Optional principal who initiated the request. Audit only.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>FluentStorage backend key used for every per-item job. Defaults to "default".</summary>
    public string? StorageKey { get; init; }
}

/// <summary>
/// Command to the Worker telling it to invoke yt-dlp's flat-playlist metadata fetch.
/// Reply: <see cref="PlaylistMetadataFetched"/> or <see cref="PlaylistMetadataFetchFailed"/>.
/// </summary>
public sealed record FetchPlaylistMetadataCommand : IPlaylistFlowMessage
{
    public required Guid PlaylistId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required string SourceUrl { get; init; }

    /// <summary>1-based playlist entry index where this bounded metadata page starts.</summary>
    public int PageStartIndex { get; init; } = FetchPlaylistMetadataCommandDefaults.PageStartIndex;

    /// <summary>Maximum number of playlist entries to resolve in this metadata page.</summary>
    public int PageSize { get; init; } = FetchPlaylistMetadataCommandDefaults.PageSize;
}

/// <summary>
/// One entry in a playlist's flat metadata response.
/// </summary>
public sealed record PlaylistEntry
{
    /// <summary>Position within the playlist (provider-reported; 1-based for YouTube).</summary>
    public required int PlaylistIndex { get; init; }

    /// <summary>URL of the individual entry (used as the per-item download SourceUrl).</summary>
    public required string EntryUrl { get; init; }

    /// <summary>Display title from the flat metadata fetch.</summary>
    public string? EntryTitle { get; init; }
}

/// <summary>
/// Event emitted by the Worker once yt-dlp resolves the playlist's entry list.
/// </summary>
public sealed record PlaylistMetadataFetched : IPlaylistFlowMessage
{
    public required Guid PlaylistId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    /// <summary>Provider's own playlist id (e.g. YouTube playlist id), when known.</summary>
    public string? ProviderPlaylistId { get; init; }

    /// <summary>Display title of the playlist.</summary>
    public string? Title { get; init; }

    /// <summary>Total entry count reported by yt-dlp.</summary>
    public int TotalItems { get; init; }

    /// <summary>1-based playlist entry index where this bounded metadata page started.</summary>
    public int PageStartIndex { get; init; } = FetchPlaylistMetadataCommandDefaults.PageStartIndex;

    /// <summary>Maximum number of entries requested for this metadata page.</summary>
    public int PageSize { get; init; } = FetchPlaylistMetadataCommandDefaults.PageSize;

    /// <summary>True when no additional playlist metadata pages need to be fetched.</summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>Next 1-based playlist entry index to fetch when <see cref="IsComplete"/> is false.</summary>
    public int? NextPageStartIndex { get; init; }

    /// <summary>The full entry list, ordered by <see cref="PlaylistEntry.PlaylistIndex"/>.</summary>
    public required IReadOnlyList<PlaylistEntry> Entries { get; init; }
}

/// <summary>
/// Event emitted when the playlist metadata fetch failed.
/// </summary>
public sealed record PlaylistMetadataFetchFailed : IPlaylistFlowMessage
{
    public required Guid PlaylistId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }

    public required FailureKind FailureKind { get; init; }
    public string? ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
}

/// <summary>
/// Internal command. Tells DataBridge to drain the staging table and create one
/// DownloadJob + PlaylistItem row per entry, publishing <see cref="DownloadRequested"/> for each.
/// </summary>
public sealed record ProcessPlaylistStagedEntriesCommand : IPlaylistFlowMessage
{
    public required Guid PlaylistId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid MessageId { get; init; }
    public required string OperationKey { get; init; }
    public required Instant OccurredAt { get; init; }
    public required int Attempt { get; init; }
}

// ── NATS request/reply (non-JetStream) for queries ────────────────────────────

public sealed class PlaylistGetRequestMessage
{
    public required Guid PlaylistId { get; init; }
}

public sealed class PlaylistListRequestMessage
{
    public int PageSize { get; init; } = 50;
    public int PageOffset { get; init; }
}

public sealed class PlaylistItemDto
{
    public required int PlaylistIndex { get; init; }
    public required Guid JobId { get; init; }
    public required string EntryUrl { get; init; }
    public string? EntryTitle { get; init; }
    public required DownloadJobState JobState { get; init; }
    public Guid? MediaGuid { get; init; }
}

public sealed class PlaylistDto
{
    public required Guid PlaylistId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required PlaylistState State { get; init; }
    public required string SourceUrl { get; init; }
    public string? RequestedBy { get; init; }
    public string? StorageKey { get; init; }
    public string? ProviderPlaylistId { get; init; }
    public string? Title { get; init; }
    public int TotalItems { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
    public Instant? CompletedAt { get; init; }
    public Instant? LastScannedAt { get; init; }

    public int CompletedItems { get; init; }
    public int FailedItems { get; init; }
    public int PendingItems { get; init; }

    /// <summary>Populated only by the GET-by-id query; null on list responses.</summary>
    public IReadOnlyList<PlaylistItemDto>? Items { get; init; }
}

public sealed class PlaylistGetResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public PlaylistDto? Playlist { get; init; }
}

public sealed class PlaylistListResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<PlaylistDto>? Items { get; init; }
}
