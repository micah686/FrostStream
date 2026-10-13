using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shared.LiveChat;

/// <summary>
/// One renderable piece of a chat message. Serialized as a JSON array per message; the same
/// shape is stored in ClickHouse and rendered by the frontend, so archived chats from any
/// platform share one renderer.
/// </summary>
public sealed record LiveChatFragment
{
    public const string TextType = "text";
    public const string EmojiType = "emoji";
    public const string EmoteType = "emote";
    public const string LinkType = "link";

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Literal text (text/link fragments).</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Unicode emoji value (emoji fragments).</summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }

    /// <summary>Platform emote id (emote fragments).</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    /// <summary>Emote shortcut/name, e.g. <c>:_catJam:</c> (emote fragments).</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>
    /// Source image URL as scraped (emote fragments). Present in freshly parsed messages;
    /// replaced by <see cref="Path"/> at ingest time and dropped from the stored JSON.
    /// </summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    /// <summary>Content-addressed blob storage path of the archived emote image.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    public static LiveChatFragment ForText(string text)
        => new() { Type = TextType, Text = text };

    public static LiveChatFragment ForEmoji(string value)
        => new() { Type = EmojiType, Value = value };

    public static LiveChatFragment ForEmote(string id, string name, string url)
        => new() { Type = EmoteType, Id = id, Name = name, Url = url };

    public static LiveChatFragment ForLink(string text, string url)
        => new() { Type = LinkType, Text = text, Url = url };
}

/// <summary>
/// Single serializer configuration for fragment arrays. The ingest side hashes the serialized
/// bytes for deduplication, so every producer must use these options.
/// </summary>
public static class LiveChatFragmentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<LiveChatFragment> fragments)
        => JsonSerializer.Serialize(fragments, Options);

    public static IReadOnlyList<LiveChatFragment> Deserialize(string json)
        => JsonSerializer.Deserialize<List<LiveChatFragment>>(json, Options) ?? [];
}
