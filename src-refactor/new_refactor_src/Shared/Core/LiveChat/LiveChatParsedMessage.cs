namespace Shared.LiveChat;

public enum LiveChatMessageType
{
    Message,
    SuperChat,
    Membership,
    Sticker,
    System,
}

public static class LiveChatMessageTypeExtensions
{
    /// <summary>Lowercase wire/storage name (matches <c>LiveChatMessageDto.Type</c>).</summary>
    public static string ToWireString(this LiveChatMessageType type) => type switch
    {
        LiveChatMessageType.SuperChat => "superchat",
        LiveChatMessageType.Membership => "membership",
        LiveChatMessageType.Sticker => "sticker",
        LiveChatMessageType.System => "system",
        _ => "message",
    };
}

/// <summary>One chat message parsed from a yt-dlp live_chat.json line.</summary>
public sealed record LiveChatParsedMessage
{
    public required string MessageId { get; init; }
    public required long VideoOffsetMs { get; init; }
    public long? TimestampUsec { get; init; }
    public required LiveChatMessageType Type { get; init; }
    public string AuthorExternalId { get; init; } = "";
    public string AuthorName { get; init; } = "";
    public IReadOnlyList<string> Badges { get; init; } = [];
    public required IReadOnlyList<LiveChatFragment> Fragments { get; init; }
    public string? AmountText { get; init; }
    public uint? HeaderColor { get; init; }
    public uint? BodyColor { get; init; }
}
