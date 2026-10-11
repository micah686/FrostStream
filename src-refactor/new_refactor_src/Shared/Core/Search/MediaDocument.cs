using System.Text.Json.Serialization;

namespace DataBridge.Search;

public sealed record MediaDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("thumbnail_storage_path")]
    public string? ThumbnailStoragePath { get; init; }

    [JsonPropertyName("account_avatar_storage_path")]
    public string? AccountAvatarStoragePath { get; init; }

    [JsonPropertyName("webpage_url")]
    public string? WebpageUrl { get; init; }

    [JsonPropertyName("release_date_unix")]
    public long? ReleaseDateUnix { get; init; }

    [JsonPropertyName("release_date_sort")]
    public long ReleaseDateSort { get; init; }

    /// <summary>
    /// When FrostStream actually scraped/ingested this metadata (metadata.media_metadata.metadata_scrape_date),
    /// as opposed to <see cref="ReleaseDateSort"/> which is the source's own publish/upload date. This is
    /// what "Recently added" sorts by.
    /// </summary>
    [JsonPropertyName("added_at_sort")]
    public long AddedAtSort { get; init; }

    [JsonPropertyName("view_count")]
    public long? ViewCount { get; init; }

    [JsonPropertyName("like_count")]
    public long? LikeCount { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("video_codec")]
    public string? VideoCodec { get; init; }

    [JsonPropertyName("audio_codec")]
    public string? AudioCodec { get; init; }

    [JsonPropertyName("video_width")]
    public int? VideoWidth { get; init; }

    [JsonPropertyName("video_height")]
    public int? VideoHeight { get; init; }

    [JsonPropertyName("resolution_label")]
    public string? ResolutionLabel { get; init; }

    [JsonPropertyName("hdr_type")]
    public string? HdrType { get; init; }

    [JsonPropertyName("audio_channels")]
    public int? AudioChannels { get; init; }

    [JsonPropertyName("was_live")]
    public bool WasLive { get; init; }

    [JsonPropertyName("availability")]
    public string? Availability { get; init; }

    [JsonPropertyName("age_limit")]
    public int? AgeLimit { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("account_id")]
    public long AccountId { get; init; }

    [JsonPropertyName("account_name")]
    public required string AccountName { get; init; }

    [JsonPropertyName("account_handle")]
    public required string AccountHandle { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get; init; } = [];

    [JsonPropertyName("genres")]
    public IReadOnlyList<string> Genres { get; init; } = [];

    [JsonPropertyName("artists")]
    public IReadOnlyList<string> Artists { get; init; } = [];

    [JsonPropertyName("caption_languages")]
    public IReadOnlyList<string> CaptionLanguages { get; init; } = [];
}
