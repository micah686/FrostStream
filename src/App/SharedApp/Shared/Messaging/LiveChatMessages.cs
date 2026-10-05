namespace Shared.Messaging;

/// <summary>
/// Queues ingestion of an archived live-chat sidecar into ClickHouse. Published to the
/// background-jobs JetStream stream by the download flow after the sidecar upload (and by the
/// backfill job); consumed by DataBridge only when live chat is enabled. Ingestion is
/// idempotent — it deletes the media's rows first — so redelivery is safe.
/// </summary>
public sealed record LiveChatIngestRequested
{
    public required Guid MediaGuid { get; init; }
    public int? VersionNum { get; init; }
    public required string StorageKey { get; init; }

    /// <summary>Blob path of the <c>media.live_chat.json</c> sidecar.</summary>
    public required string ChatBlobPath { get; init; }

    /// <summary>Blob path of the <c>media.live_chat.emotes.json</c> map; null when none exists.</summary>
    public string? EmoteMapBlobPath { get; init; }
}

/// <summary>
/// Playback-oriented chat window request. Either <c>AroundMs</c> (seek: messages before/after a
/// video offset) or <c>FromMs</c>/<c>ToMs</c> (sequential prefetch of a range) is used — when
/// <c>AroundMs</c> is set the range fields are ignored.
/// </summary>
public sealed record LiveChatWindowRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public long? AroundMs { get; init; }
    public int Before { get; init; } = 200;
    public int After { get; init; } = 400;
    public long? FromMs { get; init; }
    public long? ToMs { get; init; }
    public int Limit { get; init; } = 500;
}

public sealed record LiveChatWindowResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<LiveChatMessageDto> Messages { get; init; } = [];
}

public sealed record LiveChatMessageDto
{
    public required string MessageId { get; init; }
    public required long VideoOffsetMs { get; init; }
    public long? PublishedAtUnixMs { get; init; }

    /// <summary>message | superchat | membership | sticker | system</summary>
    public required string Type { get; init; }

    public string AuthorExternalId { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public IReadOnlyList<string> Badges { get; init; } = [];

    /// <summary>JSON array of chat fragments, stored verbatim in ClickHouse and passed through.</summary>
    public required string FragmentsJson { get; init; }

    public string? AmountText { get; init; }
    public string? Currency { get; init; }
    public uint? HeaderColor { get; init; }
    public uint? BodyColor { get; init; }
}
