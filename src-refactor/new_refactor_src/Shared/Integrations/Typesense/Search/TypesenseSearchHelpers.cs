using NodaTime;
using Shared.Messaging;

namespace DataBridge.Search;

public static class TypesenseSearchHelpers
{
    public const int MaxPageSize = 100;

    /// <summary>Fields needed to project a <see cref="MediaDocument"/> into a media card.</summary>
    public const string MediaCardIncludeFields =
        "id,title,thumbnail_storage_path,account_avatar_storage_path,duration_seconds," +
        "release_date_unix,view_count,availability,was_live,platform,account_id,account_name,account_handle";

    public static int NormalizePage(int page)
        => Math.Max(1, page);

    public static int NormalizePageSize(int pageSize, int defaultValue)
        => Math.Clamp(pageSize <= 0 ? defaultValue : pageSize, 1, MaxPageSize);

    public static string NormalizeSortOrder(string? sortOrder)
        => string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

    public static string MapMediaSortField(string? sortBy)
        => sortBy?.Trim().ToLowerInvariant() switch
        {
            // "Recently added"/"Oldest added" — when FrostStream actually ingested the media.
            "added_at" or "added_at_sort" or "recently_added" => "added_at_sort",
            // "Release date (newest/oldest)" — the source's own publish/upload date.
            "release_date" or "release_date_unix" or "release_date_sort" => "release_date_sort",
            "view_count" or "views" => "view_count",
            "like_count" or "likes" => "like_count",
            "duration" or "duration_seconds" => "duration_seconds",
            "resolution" or "height" or "video_height" => "video_height",
            "title" => "title",
            _ => "added_at_sort"
        };

    public static string MapCommentSortField(string? sortBy)
        => sortBy?.Trim().ToLowerInvariant() switch
        {
            "likes" or "like_count" => "like_count",
            "timestamp" or "comment_timestamp_unix" => "comment_timestamp_unix",
            _ => "comment_timestamp_unix"
        };

    public static string NormalizeGuid(Guid mediaGuid)
        => mediaGuid.ToString("N");

    public static string Eq(string field, string value)
        => field + ":=" + Quote(value);

    public static string Eq(string field, long value)
        => field + ":=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Ne(string field, string value)
        => field + ":!=" + Quote(value);

    public static string Quote(string value)
        => "`" + value.Replace("`", "\\`", StringComparison.Ordinal) + "`";

    public static string? BuildMediaFilter(
        string? platform,
        long? accountId,
        string? tag,
        string? category,
        string? genre,
        string? captionLanguage)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(platform))
            parts.Add(Eq("platform", platform.Trim()));
        if (accountId is { } id)
            parts.Add(Eq("account_id", id));
        if (!string.IsNullOrWhiteSpace(tag))
            parts.Add(Eq("tags", tag.Trim()));
        if (!string.IsNullOrWhiteSpace(category))
            parts.Add(Eq("categories", category.Trim()));
        if (!string.IsNullOrWhiteSpace(genre))
            parts.Add(Eq("genres", genre.Trim()));

        if (!string.IsNullOrWhiteSpace(captionLanguage))
            parts.Add(Eq("caption_languages", captionLanguage.Trim()));

        return parts.Count == 0 ? null : string.Join(" && ", parts);
    }

    public static MetadataCardDto ToCardDto(MediaDocument document)
        => ToCardDto(document, userNote: null, accountUserNote: null);

    public static MetadataCardDto ToCardDto(MediaDocument document, string? userNote)
        => ToCardDto(document, userNote, accountUserNote: null);

    public static MetadataCardDto ToCardDto(MediaDocument document, string? userNote, string? accountUserNote)
        => new()
        {
            MediaGuid = Guid.ParseExact(document.Id, "N"),
            Title = document.Title,
            ThumbnailStoragePath = document.ThumbnailStoragePath,
            DurationSeconds = document.DurationSeconds,
            ReleaseDate = document.ReleaseDateUnix is { } releaseDate
                ? Instant.FromUnixTimeSeconds(releaseDate)
                : null,
            ViewCount = document.ViewCount,
            Availability = document.Availability,
            WasLive = document.WasLive,
            Account = new MetadataAccountCardDto
            {
                AccountId = document.AccountId,
                Platform = document.Platform,
                AccountName = document.AccountName,
                AccountHandle = document.AccountHandle,
                AvatarStoragePath = document.AccountAvatarStoragePath,
                UserNote = accountUserNote
            },
            UserNote = userNote
        };

    public static CommentDto ToCommentDto(CommentDocument document)
        => new()
        {
            CommentId = document.Id,
            ParentCommentId = string.IsNullOrEmpty(document.ParentCommentId) ? null : document.ParentCommentId,
            Text = document.Text,
            CommentTimestamp = Instant.FromUnixTimeSeconds(document.CommentTimestampUnix),
            LikeCount = document.LikeCount,
            DislikeCount = document.DislikeCount,
            IsFavorited = document.IsFavorited,
            IsPinned = document.IsPinned,
            IsUploader = document.IsUploader,
            Account = new MetadataAccountCardDto
            {
                AccountId = document.AccountId,
                Platform = document.Platform,
                AccountName = document.AccountName,
                AccountHandle = document.AccountHandle,
                AvatarStoragePath = document.AccountAvatarStoragePath
            }
        };

    public static CaptionDto ToCaptionDto(CaptionDocument document)
        => new()
        {
            LanguageCode = document.LanguageCode,
            CaptionType = document.CaptionType,
            Name = document.Name,
            StoragePath = document.StoragePath
        };
}
