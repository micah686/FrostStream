using NodaTime;

namespace Shared.Messaging;

public static class StatisticsSubjects
{
    public const string Overview = "statistics.overview";
    public const string ChannelsList = "statistics.channels.list";
    public const string ChannelGet = "statistics.channels.get";
    public const string DownloadHistory = "statistics.download-history";

    public const string QueueGroup = "databridge-statistics";
}

public sealed record StatisticsOverviewRequestMessage
{
    public string? OwnerSubject { get; init; }
}

public sealed record StatisticsOverviewResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public StatisticsOverviewDto? Overview { get; init; }
}

public sealed record StatisticsChannelsListRequestMessage
{
    public int PageSize { get; init; } = 20;
    public int Page { get; init; } = 1;
    public string SortBy { get; init; } = "downloaded";
    public string SortOrder { get; init; } = "desc";
}

public sealed record StatisticsChannelsListResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<ChannelStatisticsSummaryDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed record StatisticsChannelGetRequestMessage
{
    public long CreatorSourceId { get; init; }
}

public sealed record StatisticsChannelGetResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public ChannelStatisticsDetailDto? Channel { get; init; }
}

public sealed record StatisticsDownloadHistoryRequestMessage
{
    public required Instant From { get; init; }
    public required Instant To { get; init; }
    public required string Bucket { get; init; }
}

public sealed record StatisticsDownloadHistoryResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<DownloadHistoryBucketDto> Buckets { get; init; } = [];
}

public sealed record StatisticsOverviewDto
{
    public required InventoryStatisticsDto Inventory { get; init; }
    public required WatchStatisticsDto WatchProgress { get; init; }
    public IReadOnlyList<MediaTypeStatisticsDto> MediaTypes { get; init; } = [];
    public IReadOnlyList<DownloadStateStatisticsDto> DownloadStates { get; init; } = [];
}

public sealed record InventoryStatisticsDto
{
    public long TotalMedia { get; init; }
    public long TotalChannels { get; init; }
    public long TotalCreatorSources { get; init; }
    public long TotalPlaylists { get; init; }
    public long TotalDownloads { get; init; }
    public long TotalBytes { get; init; }
    public double TotalDurationSeconds { get; init; }
}

public sealed record WatchStatisticsDto
{
    public long WatchedCount { get; init; }
    public double WatchedPercent { get; init; }
    public long UnwatchedCount { get; init; }
    public double UnwatchedPercent { get; init; }
    public double WatchProgressSeconds { get; init; }
    public double WatchProgressPercent { get; init; }
}

public sealed record MediaTypeStatisticsDto
{
    public required string Type { get; init; }
    public long Count { get; init; }
    public double DurationSeconds { get; init; }
    public long Bytes { get; init; }
}

public sealed record DownloadStateStatisticsDto
{
    public required string State { get; init; }
    public long Count { get; init; }
}

public sealed record ChannelStatisticsSummaryDto
{
    public long? CreatorSourceId { get; init; }
    public required string Platform { get; init; }
    public string? SourceType { get; init; }
    public string? SourceUrl { get; init; }
    public long? AccountId { get; init; }
    public string? AccountName { get; init; }
    public string? AccountHandle { get; init; }
    public string? AvatarStoragePath { get; init; }
    public long AvailableCount { get; init; }
    public long DownloadedCount { get; init; }
    public double DownloadedPercent { get; init; }
    public double TotalDurationSeconds { get; init; }
    public double DownloadedDurationSeconds { get; init; }
    public long TotalBytes { get; init; }
    public Instant? LastSuccessfulScanAt { get; init; }
    public Instant? LastFullScanAt { get; init; }
}

public sealed record ChannelStatisticsDetailDto
{
    public required ChannelStatisticsSummaryDto Summary { get; init; }
    public long IgnoredCount { get; init; }
    public long UnavailableCount { get; init; }
    public long RemovedCount { get; init; }
    public IReadOnlyList<MediaTypeStatisticsDto> MediaTypes { get; init; } = [];
    public IReadOnlyList<DownloadStateStatisticsDto> RecentDownloadStates { get; init; } = [];
}

public sealed record DownloadHistoryBucketDto
{
    public required Instant BucketStart { get; init; }
    public required Instant BucketEnd { get; init; }
    public long Created { get; init; }
    public long Completed { get; init; }
    public long Failed { get; init; }
    public long Cancelled { get; init; }
    public long Ignored { get; init; }
    public long BytesCompleted { get; init; }
    public double DurationCompletedSeconds { get; init; }
}
