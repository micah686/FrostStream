using System.Text.Json.Serialization;

namespace DataBridge.Search;

public sealed record CaptionDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("media_guid")]
    public required string MediaGuid { get; init; }

    [JsonPropertyName("language_code")]
    public required string LanguageCode { get; init; }

    [JsonPropertyName("caption_type")]
    public required string CaptionType { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("storage_path")]
    public required string StoragePath { get; init; }

    /// <summary>Storage backend used only while hydrating the searchable Typesense document.</summary>
    [JsonIgnore]
    public string StorageKey { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
