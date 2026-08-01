using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>A single comment from yt-dlp's <c>comments</c> array.</summary>
public sealed record CommentInfo
{
    /// <summary>The opaque comment id.</summary>
    public string? Id { get; init; }

    /// <summary>Display name of the author.</summary>
    public string? Author { get; init; }

    /// <summary>Stable id of the author.</summary>
    [JsonPropertyName("author_id")]
    public string? AuthorId { get; init; }

    /// <summary>Author thumbnail URL.</summary>
    [JsonPropertyName("author_thumbnail")]
    public string? AuthorThumbnail { get; init; }

    /// <summary>HTML-rendered comment body, when available.</summary>
    public string? Html { get; init; }

    /// <summary>Plain-text comment body.</summary>
    public string? Text { get; init; }

    /// <summary>Unix timestamp of the comment.</summary>
    public long? Timestamp { get; init; }

    /// <summary>Parent comment id, when this is a reply.</summary>
    public string? Parent { get; init; }

    /// <summary>Up-vote / like count.</summary>
    [JsonPropertyName("like_count")]
    public long? LikeCount { get; init; }

    /// <summary>Down-vote / dislike count.</summary>
    [JsonPropertyName("dislike_count")]
    public long? DislikeCount { get; init; }

    /// <summary>Whether the comment is favourited by the uploader.</summary>
    [JsonPropertyName("is_favorited")]
    public bool? IsFavorited { get; init; }

    /// <summary>Whether the comment is pinned to the top of the thread.</summary>
    [JsonPropertyName("is_pinned")]
    public bool? IsPinned { get; init; }

    /// <summary>Whether the comment author is the video's uploader.</summary>
    [JsonPropertyName("author_is_uploader")]
    public bool? AuthorIsUploader { get; init; }

    /// <summary>Captures unmodelled fields emitted by the extractor.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
