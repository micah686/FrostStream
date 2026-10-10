namespace Shared.LiveChat;

/// <summary>
/// One row of the <c>media.live_chat.emotes.json</c> sidecar the Worker writes next to the chat
/// replay: which blob the image of a custom channel emote was archived to. DataBridge uses the
/// map at ingest time to rewrite emote fragments from source URLs to durable storage paths.
/// </summary>
public sealed record LiveChatEmoteMapEntry
{
    public required string EmoteId { get; init; }
    public required string Name { get; init; }
    public required string SourceUrl { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
    public string ContentHash { get; init; } = "";
}
