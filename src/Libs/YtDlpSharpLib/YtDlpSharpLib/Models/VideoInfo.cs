using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>
/// Strongly-typed metadata returned by yt-dlp's <c>--dump-json</c> / <c>--dump-single-json</c> output.
/// </summary>
public sealed record VideoInfo
{
    /// <summary>The yt-dlp <c>_type</c> classifier.</summary>
    [JsonPropertyName("_type")]
    public MetadataType? Type { get; init; }

    /// <summary>The yt-dlp video id.</summary>
    public string? Id { get; init; }

    /// <summary>The video title.</summary>
    public string? Title { get; init; }

    /// <summary>The full video title, when distinct from <see cref="Title"/>.</summary>
    [JsonPropertyName("fulltitle")]
    public string? FullTitle { get; init; }

    /// <summary>An alternate title, when supplied by the extractor.</summary>
    [JsonPropertyName("alt_title")]
    public string? AltTitle { get; init; }

    /// <summary>The display id used in templated output filenames.</summary>
    [JsonPropertyName("display_id")]
    public string? DisplayId { get; init; }

    /// <summary>Free-form video description.</summary>
    public string? Description { get; init; }

    /// <summary>The yt-dlp extractor name (e.g., <c>"youtube"</c>).</summary>
    public string? Extractor { get; init; }

    /// <summary>The yt-dlp extractor key.</summary>
    [JsonPropertyName("extractor_key")]
    public string? ExtractorKey { get; init; }

    /// <summary>The raw upload date string from yt-dlp (typically <c>YYYYMMDD</c>).</summary>
    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; init; }

    /// <summary>The upload date parsed as a <see cref="DateOnly"/>, when available.</summary>
    [JsonIgnore]
    public DateOnly? ParsedUploadDate => DateOnly.TryParseExact(
        UploadDate,
        "yyyyMMdd",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out var d)
        ? d
        : null;

    /// <summary>Unix timestamp of upload, when supplied.</summary>
    public long? Timestamp { get; init; }

    /// <summary>Unix timestamp of release.</summary>
    [JsonPropertyName("release_timestamp")]
    public long? ReleaseTimestamp { get; init; }

    /// <summary>The release date string, when supplied.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    /// <summary>Unix timestamp of last modification.</summary>
    [JsonPropertyName("modified_timestamp")]
    public long? ModifiedTimestamp { get; init; }

    /// <summary>The last-modified date string, when supplied.</summary>
    [JsonPropertyName("modified_date")]
    public string? ModifiedDate { get; init; }

    /// <summary>Duration in seconds.</summary>
    public double? Duration { get; init; }

    /// <summary>The canonical webpage URL for the video.</summary>
    [JsonPropertyName("webpage_url")]
    public string? WebpageUrl { get; init; }

    /// <summary>Direct media URL, when supplied for a single-stream metadata payload.</summary>
    public string? Url { get; init; }

    /// <summary>The file extension yt-dlp would produce.</summary>
    [JsonPropertyName("ext")]
    public string? Extension { get; init; }

    /// <summary>The selected format description.</summary>
    public string? Format { get; init; }

    /// <summary>The selected format id.</summary>
    [JsonPropertyName("format_id")]
    public string? FormatId { get; init; }

    /// <summary>Whether the URL is a direct media link.</summary>
    public bool? Direct { get; init; }

    /// <summary>The URL of the player, when supplied.</summary>
    [JsonPropertyName("player_url")]
    public string? PlayerUrl { get; init; }

    /// <summary>Whether the video can be played in an embed.</summary>
    [JsonPropertyName("playable_in_embed")]
    public bool? PlayableInEmbed { get; init; }

    /// <summary>Channel display name.</summary>
    public string? Channel { get; init; }

    /// <summary>Channel id.</summary>
    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; init; }

    /// <summary>Channel webpage URL.</summary>
    [JsonPropertyName("channel_url")]
    public string? ChannelUrl { get; init; }

    /// <summary>Channel follower count.</summary>
    [JsonPropertyName("channel_follower_count")]
    public long? ChannelFollowerCount { get; init; }

    /// <summary>Uploader display name.</summary>
    public string? Uploader { get; init; }

    /// <summary>Uploader id.</summary>
    [JsonPropertyName("uploader_id")]
    public string? UploaderId { get; init; }

    /// <summary>Uploader webpage URL.</summary>
    [JsonPropertyName("uploader_url")]
    public string? UploaderUrl { get; init; }

    /// <summary>Creator name (single).</summary>
    public string? Creator { get; init; }

    /// <summary>Creator names (multiple).</summary>
    public IReadOnlyList<string>? Creators { get; init; }

    /// <summary>Content license, when supplied.</summary>
    public string? License { get; init; }

    /// <summary>View count.</summary>
    [JsonPropertyName("view_count")]
    public long? ViewCount { get; init; }

    /// <summary>Like count.</summary>
    [JsonPropertyName("like_count")]
    public long? LikeCount { get; init; }

    /// <summary>Dislike count, when supplied by the extractor.</summary>
    [JsonPropertyName("dislike_count")]
    public long? DislikeCount { get; init; }

    /// <summary>Repost / share count.</summary>
    [JsonPropertyName("repost_count")]
    public long? RepostCount { get; init; }

    /// <summary>Comment count.</summary>
    [JsonPropertyName("comment_count")]
    public long? CommentCount { get; init; }

    /// <summary>Concurrent viewer count for live streams.</summary>
    [JsonPropertyName("concurrent_view_count")]
    public long? ConcurrentViewCount { get; init; }

    /// <summary>Average user rating.</summary>
    [JsonPropertyName("average_rating")]
    public double? AverageRating { get; init; }

    /// <summary>Whether the stream is currently live.</summary>
    [JsonPropertyName("is_live")]
    public bool? IsLive { get; init; }

    /// <summary>Whether the video was a past live stream.</summary>
    [JsonPropertyName("was_live")]
    public bool? WasLive { get; init; }

    /// <summary>Live stream lifecycle state.</summary>
    [JsonPropertyName("live_status")]
    public LiveStatus? LiveStatus { get; init; }

    /// <summary>Video start time in seconds.</summary>
    [JsonPropertyName("start_time")]
    public double? StartTime { get; init; }

    /// <summary>Video end time in seconds.</summary>
    [JsonPropertyName("end_time")]
    public double? EndTime { get; init; }

    /// <summary>The video's availability state.</summary>
    public Availability? Availability { get; init; }

    /// <summary>Categories the video belongs to.</summary>
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>Free-form tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Cast members, for narrative content.</summary>
    public IReadOnlyList<string>? Cast { get; init; }

    /// <summary>Age limit assigned by the extractor.</summary>
    [JsonPropertyName("age_limit")]
    public int? AgeLimit { get; init; }

    /// <summary>Filming location, when supplied.</summary>
    public string? Location { get; init; }

    /// <summary>All available formats yt-dlp discovered.</summary>
    public IReadOnlyList<FormatInfo>? Formats { get; init; }

    /// <summary>All thumbnails associated with the video.</summary>
    public IReadOnlyList<ThumbnailInfo>? Thumbnails { get; init; }

    /// <summary>Chapter markers, when present.</summary>
    public IReadOnlyList<ChapterInfo>? Chapters { get; init; }

    /// <summary>Subtitle tracks discovered for the video, keyed by language code.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SubtitleTrack>>? Subtitles { get; init; }

    /// <summary>Automatic caption tracks, keyed by language code.</summary>
    [JsonPropertyName("automatic_captions")]
    public IReadOnlyDictionary<string, IReadOnlyList<SubtitleTrack>>? AutomaticCaptions { get; init; }

    /// <summary>Comment thread, when fetched.</summary>
    public IReadOnlyList<CommentInfo>? Comments { get; init; }

    /// <summary>Track title, for music content.</summary>
    public string? Track { get; init; }

    /// <summary>Track number on the album.</summary>
    [JsonPropertyName("track_number")]
    public int? TrackNumber { get; init; }

    /// <summary>Stable track id.</summary>
    [JsonPropertyName("track_id")]
    public string? TrackId { get; init; }

    /// <summary>Primary artist (single).</summary>
    public string? Artist { get; init; }

    /// <summary>Primary artists (multiple).</summary>
    public IReadOnlyList<string>? Artists { get; init; }

    /// <summary>Album name.</summary>
    public string? Album { get; init; }

    /// <summary>Album type, e.g., <c>album</c>, <c>single</c>.</summary>
    [JsonPropertyName("album_type")]
    public string? AlbumType { get; init; }

    /// <summary>Album artist (single).</summary>
    [JsonPropertyName("album_artist")]
    public string? AlbumArtist { get; init; }

    /// <summary>Album artists (multiple).</summary>
    [JsonPropertyName("album_artists")]
    public IReadOnlyList<string>? AlbumArtists { get; init; }

    /// <summary>Disc number on the album.</summary>
    [JsonPropertyName("disc_number")]
    public int? DiscNumber { get; init; }

    /// <summary>Release year, when supplied.</summary>
    [JsonPropertyName("release_year")]
    public int? ReleaseYear { get; init; }

    /// <summary>Genre.</summary>
    public string? Genre { get; init; }

    /// <summary>Playlist or container entries, when this metadata is a playlist payload.</summary>
    public IReadOnlyList<VideoInfo>? Entries { get; init; }

    /// <summary>The id of the containing playlist, when known.</summary>
    [JsonPropertyName("playlist_id")]
    public string? PlaylistId { get; init; }

    /// <summary>The title of the containing playlist, when known.</summary>
    [JsonPropertyName("playlist_title")]
    public string? PlaylistTitle { get; init; }

    /// <summary>The 1-based index of this video within its playlist.</summary>
    [JsonPropertyName("playlist_index")]
    public int? PlaylistIndex { get; init; }

    /// <summary>The total entry count of the containing playlist.</summary>
    [JsonPropertyName("playlist_count")]
    public int? PlaylistCount { get; init; }

    /// <summary>Section title, when this is a chaptered slice of a parent video.</summary>
    [JsonPropertyName("section_title")]
    public string? SectionTitle { get; init; }

    /// <summary>Section start time in seconds.</summary>
    [JsonPropertyName("section_start")]
    public double? SectionStart { get; init; }

    /// <summary>Section end time in seconds.</summary>
    [JsonPropertyName("section_end")]
    public double? SectionEnd { get; init; }

    /// <summary>The parent series, for episodic content.</summary>
    public string? Series { get; init; }

    /// <summary>The season name, for episodic content.</summary>
    public string? Season { get; init; }

    /// <summary>The 1-based season number, for episodic content.</summary>
    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; init; }

    /// <summary>The episode title, for episodic content.</summary>
    public string? Episode { get; init; }

    /// <summary>The 1-based episode number, for episodic content.</summary>
    [JsonPropertyName("episode_number")]
    public int? EpisodeNumber { get; init; }

    /// <summary>Storyboard metadata, kept as a raw <see cref="JsonElement"/>.</summary>
    public JsonElement? Storyboards { get; init; }

    /// <summary>Captures any yt-dlp metadata fields not modelled explicitly.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
