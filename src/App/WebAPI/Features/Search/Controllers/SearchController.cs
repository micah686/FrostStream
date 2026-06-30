using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Metadata.Models;

namespace WebAPI.Features.Search.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(
    IMessageBus messageBus,
    ILogger<SearchController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [HttpGet]
    [Endpoint(EndpointIds.SearchQuery)]
    [EndpointSummary("Unified advanced search")]
    [EndpointDescription("Searches archived media using advanced field:value syntax (e.g. channel:LinusTechTips, codec:h264, resolution:1080p, after:2023, duration:>600) plus free text. Matches in subtitle or comment text surface the parent media, de-duplicated and tagged with where they matched. The scope parameter (all|metadata|subtitles|comments) limits which surfaces are searched; blank queries return 400.")]
    public async Task<ActionResult<PagedMetadataResponse<SearchHitDto>>> Query(
        [FromQuery(Name = "q")] string q,
        [FromQuery] string scope = SearchScope.All,
        [FromQuery] int pageSize = 24,
        [FromQuery] int page = 1,
        [FromQuery] string? sortBy = null,
        [FromQuery] string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("Query parameter 'q' is required.");

        var response = await SendRequestAsync<SearchQueryRequestMessage, SearchQueryResponseMessage>(
            SearchSubjects.Query,
            new SearchQueryRequestMessage
            {
                Query = q,
                Scope = scope,
                PageSize = pageSize,
                Page = page,
                SortBy = sortBy,
                SortOrder = sortOrder,
                OwnerSubject = ResolveSubject()
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return SearchError(response.ErrorCode, response.ErrorMessage);

        return Ok(new PagedMetadataResponse<SearchHitDto>(
            response.Items,
            response.Page,
            response.TotalCount,
            response.HasMore));
    }

    [HttpGet("similar/{mediaGuid:guid}")]
    [Endpoint(EndpointIds.SearchSimilar)]
    [EndpointSummary("Find similar media")]
    [EndpointDescription("Returns media related to the given item using content-based similarity (shared tags, genres, categories, and creator), excluding the source item. Unknown media identifiers return 404.")]
    public async Task<ActionResult<IReadOnlyList<SearchHitDto>>> Similar(
        Guid mediaGuid,
        [FromQuery] int pageSize = 12,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<SearchSimilarRequestMessage, SearchSimilarResponseMessage>(
            SearchSubjects.Similar,
            new SearchSimilarRequestMessage
            {
                MediaGuid = mediaGuid,
                PageSize = pageSize,
                OwnerSubject = ResolveSubject()
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return SearchError(response.ErrorCode, response.ErrorMessage);

        return Ok(response.Items);
    }

    private string? ResolveSubject()
        => AuthConstants.FindSubject(User);

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
            logger.LogError(ex, "Failed processing search request on subject {Subject}", subject);
            return null;
        }
    }

    private ObjectResult SearchError(string? errorCode, string? errorMessage)
        => errorCode switch
        {
            "not_found" => NotFound(errorMessage ?? "Media item was not found."),
            "validation" => BadRequest(errorMessage ?? "Invalid search request."),
            _ => StatusCode(StatusCodes.Status500InternalServerError, errorMessage ?? "Search query failed.")
        };

    private ObjectResult ServiceUnavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
}
