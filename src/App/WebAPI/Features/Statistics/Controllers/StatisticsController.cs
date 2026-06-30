using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Statistics.Models;

namespace WebAPI.Features.Statistics.Controllers;

[ApiController]
[Route("api/statistics")]
public sealed class StatisticsController(
    IMessageBus messageBus,
    ILogger<StatisticsController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly Duration MaxDailyHistoryRange = Duration.FromDays(366 * 2);

    [HttpGet("overview")]
    [Endpoint(EndpointIds.StatisticsOverview)]
    [EndpointSummary("Get global statistics overview")]
    [EndpointDescription("Returns dashboard-ready inventory totals, media type breakdowns, download state totals, and the current user's watch-progress summary from the authoritative DataBridge read model.")]
    public async Task<ActionResult<StatisticsOverviewDto>> GetOverview(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<StatisticsOverviewRequestMessage, StatisticsOverviewResponseMessage>(
            StatisticsSubjects.Overview,
            new StatisticsOverviewRequestMessage { OwnerSubject = AuthConstants.FindSubject(User) },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return StatisticsError(response.ErrorCode, response.ErrorMessage);
        if (response.Overview is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid statistics response.");

        return Ok(response.Overview);
    }

    [HttpGet("channels")]
    [Endpoint(EndpointIds.StatisticsChannelsList)]
    [EndpointSummary("List channel statistics")]
    [EndpointDescription("Returns paginated creator-source statistics for dashboard tables, including discovered availability, downloaded coverage, duration totals, byte totals, and linked channel identity when available.")]
    public async Task<ActionResult<ChannelStatisticsListResponse>> ListChannels(
        [FromQuery] int pageSize = 20,
        [FromQuery] int page = 1,
        [FromQuery] string sortBy = "downloaded",
        [FromQuery] string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<StatisticsChannelsListRequestMessage, StatisticsChannelsListResponseMessage>(
            StatisticsSubjects.ChannelsList,
            new StatisticsChannelsListRequestMessage
            {
                PageSize = pageSize,
                Page = page,
                SortBy = sortBy,
                SortOrder = sortOrder
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return StatisticsError(response.ErrorCode, response.ErrorMessage);

        return Ok(new ChannelStatisticsListResponse(
            response.Items,
            response.Page,
            response.TotalCount,
            response.HasMore));
    }

    [HttpGet("channels/{creatorSourceId:long}")]
    [Endpoint(EndpointIds.StatisticsChannelsGet)]
    [EndpointSummary("Get channel statistics detail")]
    [EndpointDescription("Returns detailed statistics for one creator source, including discovered media status counts, downloaded coverage, media type breakdowns, and download job state counts for source items.")]
    public async Task<ActionResult<ChannelStatisticsDetailDto>> GetChannel(
        long creatorSourceId,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<StatisticsChannelGetRequestMessage, StatisticsChannelGetResponseMessage>(
            StatisticsSubjects.ChannelGet,
            new StatisticsChannelGetRequestMessage { CreatorSourceId = creatorSourceId },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return StatisticsError(response.ErrorCode, response.ErrorMessage);
        if (response.Channel is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid channel statistics response.");

        return Ok(response.Channel);
    }

    [HttpGet("download-history")]
    [Endpoint(EndpointIds.StatisticsDownloadHistory)]
    [EndpointSummary("Get download history statistics")]
    [EndpointDescription("Returns bucketed download history for frontend charts over a requested UTC time range, including created, completed, failed, cancelled, ignored, completed bytes, and completed duration totals.")]
    public async Task<ActionResult<IReadOnlyList<DownloadHistoryBucketDto>>> GetDownloadHistory(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string bucket = "day",
        CancellationToken cancellationToken = default)
    {
        if (from == default || to == default)
            return BadRequest("Query parameters 'from' and 'to' are required.");

        var normalizedBucket = bucket.Trim().ToLowerInvariant();
        if (normalizedBucket is not "day" and not "week" and not "month")
            return BadRequest("Query parameter 'bucket' must be one of: day, week, month.");

        var fromInstant = Instant.FromDateTimeOffset(from);
        var toInstant = Instant.FromDateTimeOffset(to);
        if (fromInstant >= toInstant)
            return BadRequest("Query parameter 'from' must be earlier than 'to'.");
        if (normalizedBucket == "day" && toInstant - fromInstant > MaxDailyHistoryRange)
            return BadRequest("Daily download history is limited to a two-year range.");

        var response = await SendRequestAsync<StatisticsDownloadHistoryRequestMessage, StatisticsDownloadHistoryResponseMessage>(
            StatisticsSubjects.DownloadHistory,
            new StatisticsDownloadHistoryRequestMessage
            {
                From = fromInstant,
                To = toInstant,
                Bucket = normalizedBucket
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return StatisticsError(response.ErrorCode, response.ErrorMessage);

        return Ok(response.Buckets);
    }

    private async Task<TResponse?> SendRequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(
                subject,
                request,
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing statistics request on subject {Subject}", subject);
            return null;
        }
    }

    private ObjectResult StatisticsError(string? errorCode, string? errorMessage)
        => errorCode switch
        {
            "not_found" => NotFound(errorMessage ?? "Statistics item was not found."),
            "validation" => BadRequest(errorMessage ?? "Invalid statistics request."),
            _ => StatusCode(StatusCodes.Status500InternalServerError, errorMessage ?? "Statistics query failed.")
        };

    private ObjectResult ServiceUnavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
}
