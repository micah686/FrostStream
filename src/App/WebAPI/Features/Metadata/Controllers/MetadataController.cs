using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Metadata.Models;

namespace WebAPI.Features.Metadata.Controllers;

[ApiController]
[Route("api/metadata")]
public sealed class MetadataController(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    IClock clock,
    ILogger<MetadataController> logger) : ControllerBase
{
    private const string ChannelAssetRefreshTaskType = "channel_asset_refresh";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [HttpGet]
    [Endpoint(EndpointIds.MetadataList)]
    [EndpointSummary("Browse archived media metadata")]
    [EndpointDescription("Returns a paginated collection of media cards from the authoritative metadata store. Results can be sorted and filtered by platform, creator account, tag, category, genre, or caption language; the response includes total count and whether another page is available.")]
    public async Task<ActionResult<PagedMetadataResponse<MetadataCardDto>>> List(
        [FromQuery] int pageSize = 24,
        [FromQuery] int page = 1,
        [FromQuery] string sortBy = "release_date",
        [FromQuery] string sortOrder = "desc",
        [FromQuery] string? platform = null,
        [FromQuery] long? accountId = null,
        [FromQuery] string? tag = null,
        [FromQuery] string? category = null,
        [FromQuery] string? genre = null,
        [FromQuery] string? captionLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<MetadataListRequestMessage, MetadataListResponseMessage>(
            MetadataSubjects.List,
            new MetadataListRequestMessage
            {
                PageSize = pageSize,
                Page = page,
                SortBy = sortBy,
                SortOrder = sortOrder,
                Platform = platform,
                AccountId = accountId,
                Tag = tag,
                Category = category,
                Genre = genre,
                CaptionLanguage = captionLanguage,
                OwnerSubject = ResolveSubject()
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new PagedMetadataResponse<MetadataCardDto>(
            response.Items,
            response.Page,
            response.TotalCount,
            response.HasMore));
    }

    [Obsolete("remove this endpoint later")]
    [HttpGet("search")]
    [Endpoint(EndpointIds.MetadataSearch)]
    [EndpointSummary("Search archived media metadata")]
    [EndpointDescription("Performs full-text search across indexed media metadata using the required q parameter. Results support pagination, platform and taxonomy filters, optional explicit sorting, and return total-count and continuation information; blank search queries return 400.")]
    public async Task<ActionResult<PagedMetadataResponse<MetadataCardDto>>> Search(
        [FromQuery(Name = "q")] string q,
        [FromQuery] int pageSize = 24,
        [FromQuery] int page = 1,
        [FromQuery] string? platform = null,
        [FromQuery] string? tag = null,
        [FromQuery] string? category = null,
        [FromQuery] string? genre = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("Query parameter 'q' is required.");

        var response = await SendRequestAsync<MetadataSearchRequestMessage, MetadataSearchResponseMessage>(
            MetadataSubjects.Search,
            new MetadataSearchRequestMessage
            {
                Query = q,
                PageSize = pageSize,
                Page = page,
                Platform = platform,
                Tag = tag,
                Category = category,
                Genre = genre,
                SortBy = sortBy,
                SortOrder = sortOrder,
                OwnerSubject = ResolveSubject()
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new PagedMetadataResponse<MetadataCardDto>(
            response.Items,
            response.Page,
            response.TotalCount,
            response.HasMore));
    }

    [HttpGet("{mediaGuid:guid}")]
    [Endpoint(EndpointIds.MetadataGet)]
    [EndpointSummary("Get detailed media metadata")]
    [EndpointDescription("Retrieves the complete descriptive metadata record for one archived media item by GUID. The response includes the item's core metadata and associated descriptive relationships exposed by DataBridge; unknown media identifiers return 404.")]
    public async Task<ActionResult<MetadataDetailDto>> Get(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MetadataGetRequestMessage, MetadataGetResponseMessage>(
            MetadataSubjects.Get,
            new MetadataGetRequestMessage { MediaGuid = mediaGuid, OwnerSubject = ResolveSubject() },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);
        if (response.Item is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid metadata response.");

        return Ok(response.Item);
    }

    [HttpGet("{mediaGuid:guid}/technical")]
    [Endpoint(EndpointIds.MetadataTechnical)]
    [EndpointSummary("Get technical media metadata")]
    [EndpointDescription("Retrieves technical capture and encoding details for one archived media item, separate from its descriptive metadata. Returns 404 when the media GUID has no technical record and 503 when DataBridge cannot be reached.")]
    public async Task<ActionResult<MetadataTechnicalDto>> GetTechnical(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MetadataTechnicalRequestMessage, MetadataTechnicalResponseMessage>(
            MetadataSubjects.GetTechnical,
            new MetadataTechnicalRequestMessage { MediaGuid = mediaGuid },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);
        if (response.Item is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid technical metadata response.");

        return Ok(response.Item);
    }

    [HttpGet("{mediaGuid:guid}/versions")]
    [Endpoint(EndpointIds.MetadataVersions)]
    [EndpointSummary("List media versions")]
    [EndpointDescription("Returns either the total number of stored versions for a media GUID or the full ordered list of available content versions, depending on the countOnly query parameter.")]
    public async Task<ActionResult<object>> ListVersions(
        Guid mediaGuid,
        [FromQuery] bool countOnly = false,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<MetadataVersionsRequestMessage, MetadataVersionsResponseMessage>(
            MetadataSubjects.Versions,
            new MetadataVersionsRequestMessage
            {
                MediaGuid = mediaGuid,
                CountOnly = countOnly
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return countOnly
            ? Ok(response.TotalCount)
            : Ok(new MetadataVersionsResponse(response.TotalCount, response.Items));
    }

    [HttpGet("{mediaGuid:guid}/comments")]
    [Endpoint(EndpointIds.MetadataComments)]
    [EndpointSummary("List comments for a media item")]
    [EndpointDescription("Returns paginated comments associated with a media item. Comments can be searched by text, restricted to replies under a parent comment, and sorted by a supported field and direction; the response includes total count and whether more results remain.")]
    public async Task<ActionResult<PagedMetadataResponse<CommentDto>>> ListComments(
        Guid mediaGuid,
        [FromQuery] int pageSize = 20,
        [FromQuery] int page = 1,
        [FromQuery(Name = "q")] string? q = null,
        [FromQuery] string? parentId = null,
        [FromQuery] string sortBy = "timestamp",
        [FromQuery] string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<MetadataCommentsListRequestMessage, MetadataCommentsListResponseMessage>(
            MetadataSubjects.CommentsList,
            new MetadataCommentsListRequestMessage
            {
                MediaGuid = mediaGuid,
                PageSize = pageSize,
                Page = page,
                Query = q,
                ParentCommentId = parentId,
                SortBy = sortBy,
                SortOrder = sortOrder
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new PagedMetadataResponse<CommentDto>(
            response.Items,
            response.Page,
            response.TotalCount,
            response.HasMore));
    }

    [HttpGet("{mediaGuid:guid}/captions")]
    [Endpoint(EndpointIds.MetadataCaptions)]
    [EndpointSummary("List captions for a media item")]
    [EndpointDescription("Returns caption tracks associated with a media item, optionally filtered by language code and caption type such as manual or automatic. The response includes all matching caption metadata and the total number of tracks.")]
    public async Task<ActionResult<MetadataListResponse<CaptionDto>>> ListCaptions(
        Guid mediaGuid,
        [FromQuery] string? languageCode = null,
        [FromQuery] string? captionType = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<MetadataCaptionsListRequestMessage, MetadataCaptionsListResponseMessage>(
            MetadataSubjects.CaptionsList,
            new MetadataCaptionsListRequestMessage
            {
                MediaGuid = mediaGuid,
                LanguageCode = languageCode,
                CaptionType = captionType
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new MetadataListResponse<CaptionDto>(response.Items, response.TotalCount));
    }

    [HttpGet("accounts")]
    [Endpoint(EndpointIds.MetadataAccountsList)]
    [EndpointSummary("List creator accounts")]
    [EndpointDescription("Returns creator accounts using cursor-based pagination, with an optional platform filter. Supply the returned next cursor as after to continue from the previous page; the response indicates whether more accounts are available.")]
    public async Task<ActionResult<AccountListResponse>> ListAccounts(
        [FromQuery] int pageSize = 24,
        [FromQuery] string? after = null,
        [FromQuery] string? platform = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<MetadataAccountsListRequestMessage, MetadataAccountsListResponseMessage>(
            MetadataSubjects.AccountsList,
            new MetadataAccountsListRequestMessage
            {
                PageSize = pageSize,
                After = after,
                Platform = platform,
                OwnerSubject = ResolveSubject()
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new AccountListResponse(response.Items, response.NextCursor, response.HasMore));
    }

    [HttpGet("accounts/{accountId:long}")]
    [Endpoint(EndpointIds.MetadataAccountsGet)]
    [EndpointSummary("Get a creator account")]
    [EndpointDescription("Retrieves one normalized creator account by its internal numeric identifier, including platform identity and stored profile metadata. Returns 404 when the account does not exist.")]
    public async Task<ActionResult<AccountDto>> GetAccount(long accountId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MetadataAccountGetRequestMessage, MetadataAccountGetResponseMessage>(
            MetadataSubjects.AccountsGet,
            new MetadataAccountGetRequestMessage { AccountId = accountId, OwnerSubject = ResolveSubject() },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);
        if (response.Item is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid account response.");

        return Ok(response.Item);
    }

    [HttpPost("accounts/{accountId:long}/refresh-assets")]
    [Endpoint(EndpointIds.MetadataAccountsRefreshAssets)]
    [EndpointSummary("Queue a channel asset refresh")]
    [EndpointDescription("Verifies that the creator account exists and has a stored channel URL, then queues an asynchronous download of its avatar and banner. The force query parameter controls whether cached assets may be replaced; a successful request returns 202 with the queued account identifier.")]
    public async Task<IActionResult> RefreshAccountAssets(
        long accountId,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<MetadataAccountGetRequestMessage, MetadataAccountGetResponseMessage>(
            MetadataSubjects.AccountsGet,
            new MetadataAccountGetRequestMessage { AccountId = accountId, OwnerSubject = ResolveSubject() },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);
        if (response.Item is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid account response.");
        if (string.IsNullOrWhiteSpace(response.Item.AccountUrl))
            return BadRequest("This account has no stored channel URL to refresh assets from.");

        var now = clock.GetCurrentInstant();
        var idempotencyKey = $"manual-account:{accountId}:{Guid.NewGuid():N}";
        var message = new ChannelAssetRefreshRequested
        {
            ScheduleKey = "manual",
            TaskType = ChannelAssetRefreshTaskType,
            DueWindowUtc = now,
            IdempotencyKey = idempotencyKey,
            OccurredAt = now,
            TargetAccountId = accountId,
            Force = force
        };

        try
        {
            await publisher.PublishAsync(
                BackgroundJobSubjects.ChannelAssetRefreshRequest,
                message,
                messageId: idempotencyKey,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed enqueueing channel asset refresh for account {AccountId}", accountId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to enqueue channel asset refresh.");
        }

        return Accepted(new { queued = true, accountId, force });
    }

    [HttpGet("accounts/{accountId:long}/media")]
    [Endpoint(EndpointIds.MetadataAccountsMedia)]
    [EndpointSummary("List media for a creator account")]
    [EndpointDescription("Returns a paginated collection of archived media associated with one creator account. Results can be sorted by a supported metadata field and direction and include total-count and has-more information.")]
    public async Task<ActionResult<PagedMetadataResponse<MetadataCardDto>>> ListAccountMedia(
        long accountId,
        [FromQuery] int pageSize = 24,
        [FromQuery] int page = 1,
        [FromQuery] string sortBy = "release_date",
        [FromQuery] string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<MetadataListRequestMessage, MetadataListResponseMessage>(
            MetadataSubjects.AccountsMediaList,
            new MetadataListRequestMessage
            {
                PageSize = pageSize,
                Page = page,
                SortBy = sortBy,
                SortOrder = sortOrder,
                AccountId = accountId
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new PagedMetadataResponse<MetadataCardDto>(
            response.Items,
            response.Page,
            response.TotalCount,
            response.HasMore));
    }

    [HttpGet("taxonomy/tags")]
    [Endpoint(EndpointIds.MetadataTaxonomyTags)]
    [EndpointSummary("List metadata tags")]
    [EndpointDescription("Returns a paginated list of distinct metadata tags and their usage information. The optional search term filters tag values, while pageSize and pageOffset control offset pagination.")]
    public Task<ActionResult<TaxonomyListResponse>> ListTags(
        [FromQuery] int pageSize = 100,
        [FromQuery] int pageOffset = 0,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
        => ListTaxonomy(MetadataSubjects.TaxonomyTagsList, pageSize, pageOffset, search, cancellationToken);

    [HttpGet("taxonomy/categories")]
    [Endpoint(EndpointIds.MetadataTaxonomyCategories)]
    [EndpointSummary("List metadata categories")]
    [EndpointDescription("Returns a paginated list of distinct metadata categories and their usage information. The optional search term filters category values, while pageSize and pageOffset control offset pagination.")]
    public Task<ActionResult<TaxonomyListResponse>> ListCategories(
        [FromQuery] int pageSize = 100,
        [FromQuery] int pageOffset = 0,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
        => ListTaxonomy(MetadataSubjects.TaxonomyCategoriesList, pageSize, pageOffset, search, cancellationToken);

    [HttpGet("taxonomy/genres")]
    [Endpoint(EndpointIds.MetadataTaxonomyGenres)]
    [EndpointSummary("List metadata genres")]
    [EndpointDescription("Returns a paginated list of distinct metadata genres and their usage information. The optional search term filters genre values, while pageSize and pageOffset control offset pagination.")]
    public Task<ActionResult<TaxonomyListResponse>> ListGenres(
        [FromQuery] int pageSize = 100,
        [FromQuery] int pageOffset = 0,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
        => ListTaxonomy(MetadataSubjects.TaxonomyGenresList, pageSize, pageOffset, search, cancellationToken);

    private async Task<ActionResult<TaxonomyListResponse>> ListTaxonomy(
        string subject,
        int pageSize,
        int pageOffset,
        string? search,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MetadataTaxonomyListRequestMessage, MetadataTaxonomyListResponseMessage>(
            subject,
            new MetadataTaxonomyListRequestMessage
            {
                PageSize = pageSize,
                PageOffset = pageOffset,
                Search = search
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new TaxonomyListResponse(response.Items, response.Total));
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
            logger.LogError(ex, "Failed processing metadata request on subject {Subject}", subject);
            return null;
        }
    }

    private ObjectResult MetadataError(string? errorCode, string? errorMessage)
        => errorCode switch
        {
            "not_found" => NotFound(errorMessage ?? "Metadata item was not found."),
            "invalid_cursor" or "validation" => BadRequest(errorMessage ?? "Invalid metadata request."),
            _ => StatusCode(StatusCodes.Status500InternalServerError, errorMessage ?? "Metadata query failed.")
        };

    private ObjectResult ServiceUnavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
}
