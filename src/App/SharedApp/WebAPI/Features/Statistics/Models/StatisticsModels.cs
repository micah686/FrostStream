using Shared.Messaging;

namespace WebAPI.Features.Statistics.Models;

public sealed record ChannelStatisticsListResponse(
    IReadOnlyList<ChannelStatisticsSummaryDto> Items,
    int Page,
    int TotalCount,
    bool HasMore);
