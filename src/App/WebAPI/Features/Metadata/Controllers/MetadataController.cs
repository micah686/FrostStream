using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;
using WebAPI.Features.Metadata.Models;

namespace WebAPI.Features.Metadata.Controllers;

[ApiController]
[Route("api/metadata")]
public sealed class MetadataController(
    IMessageBus messageBus,
    ILogger<MetadataController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [HttpGet]
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
                CaptionLanguage = captionLanguage
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

    [HttpGet("search")]
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
                SortOrder = sortOrder
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
    public async Task<ActionResult<MetadataDetailDto>> Get(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MetadataGetRequestMessage, MetadataGetResponseMessage>(
            MetadataSubjects.Get,
            new MetadataGetRequestMessage { MediaGuid = mediaGuid },
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

    [HttpGet("{mediaGuid:guid}/comments")]
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
                Platform = platform
            },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);

        return Ok(new AccountListResponse(response.Items, response.NextCursor, response.HasMore));
    }

    [HttpGet("accounts/{accountId:long}")]
    public async Task<ActionResult<AccountDto>> GetAccount(long accountId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<MetadataAccountGetRequestMessage, MetadataAccountGetResponseMessage>(
            MetadataSubjects.AccountsGet,
            new MetadataAccountGetRequestMessage { AccountId = accountId },
            cancellationToken);

        if (response is null)
            return ServiceUnavailable();
        if (!response.Success)
            return MetadataError(response.ErrorCode, response.ErrorMessage);
        if (response.Item is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid account response.");

        return Ok(response.Item);
    }

    [HttpGet("accounts/{accountId:long}/media")]
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
    public Task<ActionResult<TaxonomyListResponse>> ListTags(
        [FromQuery] int pageSize = 100,
        [FromQuery] int pageOffset = 0,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
        => ListTaxonomy(MetadataSubjects.TaxonomyTagsList, pageSize, pageOffset, search, cancellationToken);

    [HttpGet("taxonomy/categories")]
    public Task<ActionResult<TaxonomyListResponse>> ListCategories(
        [FromQuery] int pageSize = 100,
        [FromQuery] int pageOffset = 0,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
        => ListTaxonomy(MetadataSubjects.TaxonomyCategoriesList, pageSize, pageOffset, search, cancellationToken);

    [HttpGet("taxonomy/genres")]
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
