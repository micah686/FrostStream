using NodaTime;

namespace Shared.Messaging;

public sealed record MetadataAccountCardDto
{
    public required long AccountId { get; init; }
    public required string Platform { get; init; }
    public required string AccountName { get; init; }
    public required string AccountHandle { get; init; }
    public string? AvatarStoragePath { get; init; }
    public string? UserNote { get; init; }
}

public sealed record MetadataCardDto
{
    public required Guid MediaGuid { get; init; }
    public required string Title { get; init; }
    public string? ThumbnailStoragePath { get; init; }
    public double? DurationSeconds { get; init; }
    public Instant? ReleaseDate { get; init; }
    public long? ViewCount { get; init; }
    public string? Availability { get; init; }
    public bool WasLive { get; init; }
    public required MetadataAccountCardDto Account { get; init; }
    public string? UserNote { get; init; }
}

public sealed record AccountDto
{
    public required long AccountId { get; init; }
    public required string Platform { get; init; }
    public required string AccountName { get; init; }
    public required string AccountHandle { get; init; }
    public string? AccountUrl { get; init; }
    public Instant? AccountCreationDate { get; init; }
    public long? FollowerCount { get; init; }
    public bool IsVerified { get; init; }
    public string? Description { get; init; }
    public string? AvatarStoragePath { get; init; }
    public string? BannerStoragePath { get; init; }
    public long MediaCount { get; init; }
    public string? UserNote { get; init; }
}

public sealed record AccountSummaryDto
{
    public required long AccountId { get; init; }
    public required string Platform { get; init; }
    public required string AccountName { get; init; }
    public required string AccountHandle { get; init; }
    public string? AccountUrl { get; init; }
    public long? FollowerCount { get; init; }
    public bool IsVerified { get; init; }
    public string? AvatarStoragePath { get; init; }
    public long MediaCount { get; init; }
    public string? UserNote { get; init; }
}

public sealed record CaptionLanguageDto
{
    public required string LanguageCode { get; init; }
    public required string CaptionType { get; init; }
    public string? Name { get; init; }
}

public sealed record SeriesDto
{
    public required string SeriesName { get; init; }
    public int? SeasonCount { get; init; }
    public int SeasonNumber { get; init; }
    public string? SeasonName { get; init; }
    public int EpisodeNumber { get; init; }
    public required string EpisodeName { get; init; }
}

public sealed record MusicDto
{
    public required string AlbumTitle { get; init; }
    public string? AlbumType { get; init; }
    public int? DiscNumber { get; init; }
    public int? ReleaseYear { get; init; }
    public required string TrackTitle { get; init; }
    public int TrackNumber { get; init; }
    public string? Composer { get; init; }
}

public sealed record MetadataDetailDto
{
    public required Guid MediaGuid { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? ThumbnailStoragePath { get; init; }
    public double? DurationSeconds { get; init; }
    public Instant? ReleaseDate { get; init; }
    public long? ViewCount { get; init; }
    public long? LikeCount { get; init; }
    public long? DislikeCount { get; init; }
    public double? AverageRating { get; init; }
    public long? CommentCount { get; init; }
    public int? AgeLimit { get; init; }
    public bool WasLive { get; init; }
    public string? Availability { get; init; }
    public string? Location { get; init; }
    public string? WebpageUrl { get; init; }
    public string? ExternalMediaId { get; init; }
    public required Instant MetadataScrapedAt { get; init; }
    public required AccountDto Account { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Cast { get; init; } = [];
    public IReadOnlyList<string> Artists { get; init; } = [];
    public IReadOnlyList<string> AlbumArtists { get; init; } = [];
    public SeriesDto? Series { get; init; }
    public MusicDto? Music { get; init; }
    public IReadOnlyList<CaptionLanguageDto> CaptionLanguages { get; init; } = [];
    public string? UserNote { get; init; }
}

public sealed record TechnicalFormatDto
{
    public long DurationTicks { get; init; }
    public long StartTimeTicks { get; init; }
    public required string FormatLongNames { get; init; }
    public int StreamCount { get; init; }
    public double BitRate { get; init; }
}

public sealed record VideoStreamDetailDto
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double AvgFrameRate { get; init; }
    public required string HdrType { get; init; }
    public required string ColorSpace { get; init; }
    public required string Profile { get; init; }
}

public sealed record AudioStreamDetailDto
{
    public int Channels { get; init; }
    public required string ChannelLayout { get; init; }
    public int SampleRateHz { get; init; }
    public required string Profile { get; init; }
}

public sealed record TechnicalStreamDto
{
    public required string StreamType { get; init; }
    public bool IsPrimary { get; init; }
    public required string CodecName { get; init; }
    public required string CodecLongName { get; init; }
    public long BitRate { get; init; }
    public int? BitDepth { get; init; }
    public long DurationTicks { get; init; }
    public string? Language { get; init; }
    public VideoStreamDetailDto? Video { get; init; }
    public AudioStreamDetailDto? Audio { get; init; }
}

public sealed record TechnicalChapterDto
{
    public required string Title { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
}

public sealed record MetadataTechnicalDto
{
    public required Guid MediaGuid { get; init; }
    public long DurationTicks { get; init; }
    public TechnicalFormatDto? Format { get; init; }
    public IReadOnlyList<TechnicalStreamDto> Streams { get; init; } = [];
    public IReadOnlyList<TechnicalChapterDto> Chapters { get; init; } = [];
}

public sealed record MetadataVersionDto
{
    public required Guid MediaGuid { get; init; }
    public required int VersionNum { get; init; }
    public required string StorageKey { get; init; }
    public required string StoragePath { get; init; }
    public required string ContentHashXxh128 { get; init; }
    public required string IngestOrigin { get; init; }
}

public sealed record CommentDto
{
    public required string CommentId { get; init; }
    public string? ParentCommentId { get; init; }
    public required string Text { get; init; }
    public required Instant CommentTimestamp { get; init; }
    public int? LikeCount { get; init; }
    public int? DislikeCount { get; init; }
    public bool IsFavorited { get; init; }
    public bool IsPinned { get; init; }
    public bool IsUploader { get; init; }
    public required MetadataAccountCardDto Account { get; init; }
}

public sealed record CaptionDto
{
    public required string LanguageCode { get; init; }
    public required string CaptionType { get; init; }
    public string? Name { get; init; }
    public required string StoragePath { get; init; }
}

public sealed record TaxonomyItemDto
{
    public required string Name { get; init; }
    public long MediaCount { get; init; }
}

public sealed record MetadataListRequestMessage
{
    public int PageSize { get; init; } = 24;
    public int Page { get; init; } = 1;
    public string SortBy { get; init; } = "release_date";
    public string SortOrder { get; init; } = "desc";
    public string? Platform { get; init; }
    public long? AccountId { get; init; }
    public string? Tag { get; init; }
    public string? Category { get; init; }
    public string? Genre { get; init; }
    public string? CaptionLanguage { get; init; }
    public string? OwnerSubject { get; init; }
}

public sealed record MetadataListResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<MetadataCardDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed record MetadataSearchRequestMessage
{
    public required string Query { get; init; }
    public int PageSize { get; init; } = 24;
    public int Page { get; init; } = 1;
    public string? Platform { get; init; }
    public string? Tag { get; init; }
    public string? Category { get; init; }
    public string? Genre { get; init; }
    public string? SortBy { get; init; }
    public string SortOrder { get; init; } = "desc";
    public string? OwnerSubject { get; init; }
}

public sealed record MetadataSearchResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<MetadataCardDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed record MetadataGetRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public string? OwnerSubject { get; init; }
}

public sealed record MetadataGetResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MetadataDetailDto? Item { get; init; }
}

public sealed record MetadataTechnicalRequestMessage
{
    public required Guid MediaGuid { get; init; }
}

public sealed record MetadataTechnicalResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MetadataTechnicalDto? Item { get; init; }
}

public sealed record MetadataVersionsRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public bool CountOnly { get; init; }
}

public sealed record MetadataVersionsResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<MetadataVersionDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed record MetadataCommentsListRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public int PageSize { get; init; } = 20;
    public int Page { get; init; } = 1;
    public string? Query { get; init; }
    public string? ParentCommentId { get; init; }
    public string SortBy { get; init; } = "timestamp";
    public string SortOrder { get; init; } = "desc";
}

public sealed record MetadataCommentsListResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<CommentDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed record MetadataCaptionsListRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public string? LanguageCode { get; init; }
    public string? CaptionType { get; init; }
}

public sealed record MetadataCaptionsListResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<CaptionDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed record MetadataAccountsListRequestMessage
{
    public int PageSize { get; init; } = 24;
    public string? After { get; init; }
    public string? Platform { get; init; }
    public string? OwnerSubject { get; init; }
}

public sealed record MetadataAccountsListResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<AccountSummaryDto> Items { get; init; } = [];
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

public sealed record MetadataAccountGetRequestMessage
{
    public required long AccountId { get; init; }
    public string? OwnerSubject { get; init; }
}

public sealed record MetadataAccountGetResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public AccountDto? Item { get; init; }
}

/// <summary>
/// Persists freshly downloaded avatar/banner blobs onto an existing <c>metadata.accounts</c> row
/// by id. Null paths leave the existing value untouched, mirroring the (platform, handle) upsert.
/// </summary>
public sealed record MetadataAccountAssetsUpdateRequestMessage
{
    public required long AccountId { get; init; }
    public string? AvatarStoragePath { get; init; }
    public string? BannerStoragePath { get; init; }
    public string? StorageKey { get; init; }
}

public sealed record MetadataAccountAssetsUpdateResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record MetadataTaxonomyListRequestMessage
{
    public int PageSize { get; init; } = 100;
    public int PageOffset { get; init; }
    public string? Search { get; init; }
}

public sealed record MetadataTaxonomyListResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<TaxonomyItemDto> Items { get; init; } = [];
    public int Total { get; init; }
}
