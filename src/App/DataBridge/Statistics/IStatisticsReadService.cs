using Shared.Messaging;

namespace DataBridge.Statistics;

public interface IStatisticsReadService
{
    Task<StatisticsOverviewDto> GetOverviewAsync(string? ownerSubject, CancellationToken ct = default);

    Task<(IReadOnlyList<ChannelStatisticsSummaryDto> Items, int TotalCount, int Page, bool HasMore)> ListChannelsAsync(
        int pageSize,
        int page,
        string sortBy,
        string sortOrder,
        CancellationToken ct = default);

    Task<ChannelStatisticsDetailDto?> GetChannelAsync(long creatorSourceId, CancellationToken ct = default);

    Task<IReadOnlyList<DownloadHistoryBucketDto>> GetDownloadHistoryAsync(
        StatisticsDownloadHistoryRequestMessage request,
        CancellationToken ct = default);
}
