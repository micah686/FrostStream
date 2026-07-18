using NodaTime;

namespace Shared.Metadata;

public sealed record CapturedMediaMetadata
{
    public required CapturedAccountMetadata Account { get; init; }
    public required CapturedMediaMetadataCore Media { get; init; }
    public required CapturedMediaTechnicalMetadata Technical { get; init; }
    public IReadOnlyList<CapturedCaptionMetadata> Captions { get; init; } = [];
    public IReadOnlyList<CapturedCommentMetadata> Comments { get; init; } = [];
    public CapturedSeriesMetadata? Series { get; init; }
    public CapturedMusicMetadata? Music { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public IReadOnlyList<string> AlbumArtists { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Cast { get; init; } = [];
}

public sealed record CapturedAccountMetadata
{
    public required string Platform { get; init; }
    public required string AccountName { get; init; }
    public required string AccountHandle { get; init; }
    public string? AccountUrl { get; init; }
    public long? FollowerCount { get; init; }
    public string? Description { get; init; }
}

public sealed record CapturedMediaMetadataCore
{
    public string? ExternalMediaId { get; init; }
    public required Instant MetadataScrapeDate { get; init; }
    public string? ThumbnailStoragePath { get; init; }
    public int? AgeLimit { get; init; }
    public double? AverageRating { get; init; }
    public long? LikeCount { get; init; }
    public long? DislikeCount { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Description { get; init; }
    public Instant? ReleaseDate { get; init; }
    public string? Title { get; init; }
    public bool WasLive { get; init; }
    public string? WebpageUrl { get; init; }
    public long? ViewCount { get; init; }
    public long? CommentCount { get; init; }
    /// <summary>Lowercase snake_case string matching the DB <c>metadata.availability_enum</c> values.</summary>
    public string? Availability { get; init; }
    public string? Location { get; init; }
}

public sealed record CapturedMediaTechnicalMetadata
{
    public long DurationTicks { get; init; }
    public required CapturedFormatMetadata Format { get; init; }
    public IReadOnlyList<CapturedStreamMetadata> Streams { get; init; } = [];
    public IReadOnlyList<CapturedChapterMetadata> Chapters { get; init; } = [];
}

public sealed record CapturedFormatMetadata
{
    public long DurationTicks { get; init; }
    public long StartTimeTicks { get; init; }
    public required string FormatLongNames { get; init; }
    public int StreamCount { get; init; }
    public double BitRate { get; init; }
}

public sealed record CapturedStreamMetadata
{
    public required string StreamType { get; init; }
    public bool IsPrimary { get; init; }
    public required string CodecName { get; init; }
    public required string CodecLongName { get; init; }
    public long BitRate { get; init; }
    public int? BitDepth { get; init; }
    public long DurationTicks { get; init; }
    public string? Language { get; init; }
    public CapturedVideoStreamMetadata? Video { get; init; }
    public CapturedAudioStreamMetadata? Audio { get; init; }
}

public sealed record CapturedVideoStreamMetadata
{
    public double AvgFrameRate { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string HdrType { get; init; } = "SDR";
}

public sealed record CapturedAudioStreamMetadata
{
    public int Channels { get; init; }
    public int SampleRateHz { get; init; }
}

public sealed record CapturedChapterMetadata
{
    public required string Title { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
}

public sealed record CapturedCaptionMetadata
{
    public required string StoragePath { get; init; }
    /// <summary>"subtitles" or "automatic_captions" matching <c>metadata.subtitle_type_enum</c>.</summary>
    public required string CaptionType { get; init; }
    public required string LanguageCode { get; init; }
    public string? Name { get; init; }
}

public sealed record CapturedCommentMetadata
{
    public required string CommentId { get; init; }
    public string? ParentCommentId { get; init; }
    public required string Text { get; init; }
    public required CapturedAccountMetadata Account { get; init; }
    public required Instant CommentTimestamp { get; init; }
    /// <summary>Stored as <c>int?</c> to match the DB column (<c>AsInt32</c>).</summary>
    public int? LikeCount { get; init; }
    public int? DislikeCount { get; init; }
    public bool IsFavorited { get; init; }
    public bool IsPinned { get; init; }
    public bool IsUploader { get; init; }
}

public sealed record CapturedSeriesMetadata
{
    public required string SeriesName { get; init; }
    public int? SeasonCount { get; init; }
    public int SeasonNumber { get; init; }
    public string? SeasonName { get; init; }
    public int EpisodeNumber { get; init; }
    public required string EpisodeName { get; init; }
}

public sealed record CapturedMusicMetadata
{
    public required string AlbumTitle { get; init; }
    public string? AlbumType { get; init; }
    public int? DiscNumber { get; init; }
    public int? ReleaseYear { get; init; }
    public required string TrackTitle { get; init; }
    public int TrackNumber { get; init; }
    public string? Composer { get; init; }
}
