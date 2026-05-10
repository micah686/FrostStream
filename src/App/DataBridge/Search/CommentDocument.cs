using System.Text.Json.Serialization;

namespace DataBridge.Search;

public sealed record CommentDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("media_guid")]
    public required string MediaGuid { get; init; }

    [JsonPropertyName("parent_comment_id")]
    public required string ParentCommentId { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("comment_timestamp_unix")]
    public long CommentTimestampUnix { get; init; }

    [JsonPropertyName("like_count")]
    public int? LikeCount { get; init; }

    [JsonPropertyName("dislike_count")]
    public int? DislikeCount { get; init; }

    [JsonPropertyName("is_favorited")]
    public bool IsFavorited { get; init; }

    [JsonPropertyName("is_pinned")]
    public bool IsPinned { get; init; }

    [JsonPropertyName("is_uploader")]
    public bool IsUploader { get; init; }

    [JsonPropertyName("account_id")]
    public long AccountId { get; init; }

    [JsonPropertyName("account_name")]
    public required string AccountName { get; init; }

    [JsonPropertyName("account_handle")]
    public required string AccountHandle { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("account_avatar_storage_path")]
    public string? AccountAvatarStoragePath { get; init; }
}
