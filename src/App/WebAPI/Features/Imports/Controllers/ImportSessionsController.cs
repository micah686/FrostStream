using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using System.Text.Json;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Imports.Controllers;

[ApiController]
[Route("api/global/imports/sessions")]
public sealed class ImportSessionsController(
    IMessageBus messageBus,
    Func<string, IObjectStore> objectStoreFactory,
    ILogger<ImportSessionsController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const long MaxMappingBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions MappingTemplateJsonOptions = CreateMappingTemplateJsonOptions();

    [HttpPost]
    [Endpoint(EndpointIds.ImportsSessionsCreate)]
    [EndpointSummary("Create a local media import session and start scanning")]
    [EndpointDescription("Creates a DB-backed import session, publishes a worker scan command for the configured incoming folder or sub-path, and returns the initial scanning session snapshot. The scan results are ingested asynchronously into the review queue.")]
    public async Task<ActionResult<ImportSessionDto>> Create(
        [FromBody] ImportSessionCreateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionCreateRequest, ImportSessionCreateResponse>(
            ImportSessionSubjects.Create,
            request with { RequestedBy = AuthConstants.FindSubject(User) ?? request.RequestedBy },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);
        if (response.Session is null)
            return BadGateway("DataBridge returned an empty import session.");

        return Accepted(response.Session);
    }

    [HttpGet]
    [Endpoint(EndpointIds.ImportsSessionsList)]
    [EndpointSummary("List local media import sessions")]
    [EndpointDescription("Returns recent local media import sessions from DataBridge with optional status filtering and keyset pagination. Each session includes persisted counters for review and commit progress.")]
    public async Task<ActionResult<ImportSessionListResponse>> List(
        [FromQuery] ImportSessionStatus? status,
        [FromQuery] int limit = 50,
        [FromQuery] Guid? afterSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<ImportSessionListRequest, ImportSessionListResponse>(
            ImportSessionSubjects.List,
            new ImportSessionListRequest { Status = status, Limit = limit, AfterSessionId = afterSessionId },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpGet("{sessionId:guid}")]
    [Endpoint(EndpointIds.ImportsSessionsGet)]
    [EndpointSummary("Get a local media import session")]
    [EndpointDescription("Returns one local media import session by id, including lifecycle status, source, destination storage, counters, and any scan failure message.")]
    public async Task<ActionResult<ImportSessionDto>> Get(Guid sessionId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionGetRequest, ImportSessionGetResponse>(
            ImportSessionSubjects.Get,
            new ImportSessionGetRequest { SessionId = sessionId },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);
        if (response.Session is null)
            return BadGateway("DataBridge returned an empty import session.");

        return Ok(response.Session);
    }

    [HttpGet("{sessionId:guid}/items")]
    [Endpoint(EndpointIds.ImportsSessionsItemsList)]
    [EndpointSummary("List local media import session items")]
    [EndpointDescription("Returns a keyset-paged slice of discovered files for an import session, with optional inclusion, status, metadata-state, and text search filters for the wizard lists.")]
    public async Task<ActionResult<ImportSessionItemsListResponse>> ListItems(
        Guid sessionId,
        [FromQuery] ImportSessionItemStatus? status,
        [FromQuery] ImportSessionItemMetadataState? metadataState,
        [FromQuery] bool? included,
        [FromQuery] string? search,
        [FromQuery] Guid? afterItemId,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<ImportSessionItemsListRequest, ImportSessionItemsListResponse>(
            ImportSessionSubjects.ItemsList,
            new ImportSessionItemsListRequest
            {
                SessionId = sessionId,
                Status = status,
                MetadataState = metadataState,
                Included = included,
                Search = search,
                AfterItemId = afterItemId,
                Limit = limit
            },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpPatch("{sessionId:guid}/items/{itemId:guid}")]
    [Endpoint(EndpointIds.ImportsSessionsItemsPatch)]
    [EndpointSummary("Edit one local media import item")]
    [EndpointDescription("Writes user-provided metadata fields for one discovered import item. User edits mark the item metadata state as edited and take precedence during later commit.")]
    public async Task<ActionResult<ImportSessionItemPatchResponse>> PatchItem(
        Guid sessionId,
        Guid itemId,
        [FromBody] ImportSessionItemPatchBody request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionItemPatchRequest, ImportSessionItemPatchResponse>(
            ImportSessionSubjects.ItemsPatch,
            new ImportSessionItemPatchRequest
            {
                SessionId = sessionId,
                ItemId = itemId,
                Title = request.Title,
                Provider = request.Provider,
                SourceMediaId = request.SourceMediaId,
                SourceUrl = request.SourceUrl
            },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/items/bulk")]
    [Endpoint(EndpointIds.ImportsSessionsItemsBulk)]
    [EndpointSummary("Apply a bulk action to local media import items")]
    [EndpointDescription("Applies a review action to item ids or to a filtered item set. Supported actions include accepting placeholders, excluding, including, and resetting failed items.")]
    public async Task<ActionResult<ImportSessionItemsBulkResponse>> BulkItems(
        Guid sessionId,
        [FromBody] ImportSessionItemsBulkBody request,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionItemsBulkRequest, ImportSessionItemsBulkResponse>(
            ImportSessionSubjects.ItemsBulk,
            new ImportSessionItemsBulkRequest
            {
                SessionId = sessionId,
                Action = request.Action,
                ItemIds = request.ItemIds,
                Status = request.Status,
                MetadataState = request.MetadataState,
                Search = request.Search
            },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/mapping")]
    [RequestSizeLimit(MaxMappingBytes)]
    [Endpoint(EndpointIds.ImportsSessionsMapping)]
    [EndpointSummary("Apply a CSV or JSON metadata mapping file")]
    [EndpointDescription("Uploads a CSV or JSON mapping file of filename/relative-path to metadata fields, stages it in the import object-store bucket, and applies matching rows only to selected items that do not already have yt-dlp metadata.")]
    public async Task<ActionResult<ImportSessionMappingApplyResponse>> ApplyMapping(
        Guid sessionId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
            return BadRequest(new ProblemDetails { Title = "Mapping file is empty.", Status = StatusCodes.Status400BadRequest });
        if (file.Length > MaxMappingBytes)
            return BadRequest(new ProblemDetails { Title = "Mapping file must be 10 MB or smaller.", Status = StatusCodes.Status400BadRequest });

        var format = ResolveMappingFormat(file.FileName, file.ContentType);
        if (format is null)
            return BadRequest(new ProblemDetails { Title = "Mapping file must be CSV or JSON.", Status = StatusCodes.Status400BadRequest });

        var objectKey = $"mappings/{sessionId:N}/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{Path.GetFileName(file.FileName)}";
        try
        {
            var objectStore = objectStoreFactory(LocalImportTopology.ManifestObjectStoreBucket);
            await using (var stream = file.OpenReadStream())
            {
                await objectStore.PutAsync(objectKey, stream, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed staging import mapping upload for session {SessionId}.", sessionId);
            return BadGateway("Failed to stage mapping upload.");
        }

        var response = await SendAsync<ImportSessionMappingApplyRequest, ImportSessionMappingApplyResponse>(
            ImportSessionSubjects.MappingApply,
            new ImportSessionMappingApplyRequest
            {
                SessionId = sessionId,
                ObjectBucket = LocalImportTopology.ManifestObjectStoreBucket,
                ObjectKey = objectKey,
                Format = format
            },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpGet("{sessionId:guid}/mapping-template")]
    [Endpoint(EndpointIds.ImportsSessionsMappingTemplate)]
    [EndpointSummary("Download a JSON mapping template")]
    [EndpointDescription("Generates an indented JSON template for selected files that do not already have yt-dlp metadata. The template can be edited offline and uploaded through the mapping endpoint.")]
    public async Task<IActionResult> MappingTemplate(Guid sessionId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionMappingTemplateRequest, ImportSessionMappingTemplateResponse>(
            ImportSessionSubjects.MappingTemplate,
            new ImportSessionMappingTemplateRequest { SessionId = sessionId },
            cancellationToken);
        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        var json = JsonSerializer.SerializeToUtf8Bytes(response.Items, MappingTemplateJsonOptions);
        return File(json, "application/json", $"froststream-import-{sessionId:N}-mapping.json");
    }

    private static JsonSerializerOptions CreateMappingTemplateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }

    [HttpPost("{sessionId:guid}/metadata-refresh")]
    [Endpoint(EndpointIds.ImportsSessionsMetadataRefresh)]
    [EndpointSummary("Refresh local import metadata sidecars")]
    [EndpointDescription("Checks the worker incoming folder for adjacent .info.json sidecars beside selected import media files. Found sidecars are parsed as yt-dlp metadata and update the item metadata source without downloading anything.")]
    public async Task<ActionResult<ImportSessionMetadataRefreshResponse>> RefreshMetadata(
        Guid sessionId,
        [FromBody] ImportSessionMetadataRefreshBody? body,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionMetadataRefreshRequest, ImportSessionMetadataRefreshResponse>(
            ImportSessionSubjects.MetadataRefresh,
            new ImportSessionMetadataRefreshRequest
            {
                SessionId = sessionId,
                ItemIds = body?.ItemIds
            },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/enrich")]
    [Endpoint(EndpointIds.ImportsSessionsEnrich)]
    [EndpointSummary("Queue yt-dlp metadata enrichment for import items")]
    [EndpointDescription("Queues optional metadata-only yt-dlp work for selected items that carry a source URL. The restricted options allow proxy and authentication settings, HTTP headers, compatibility switches, and a minimum three-second request delay; media download is always skipped and info.json is written beside the source file.")]
    public async Task<ActionResult<ImportSessionEnrichResponse>> Enrich(
        Guid sessionId,
        [FromBody] ImportSessionEnrichBody? body,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionEnrichRequest, ImportSessionEnrichResponse>(
            ImportSessionSubjects.Enrich,
            new ImportSessionEnrichRequest
            {
                SessionId = sessionId,
                ItemIds = body?.ItemIds,
                Options = body?.Options ?? new ImportSessionYtDlpOptions()
            },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Accepted(response);
    }

    [HttpPatch("{sessionId:guid}/options")]
    [Endpoint(EndpointIds.ImportsSessionsUpdateOptions)]
    [EndpointSummary("Update import session options")]
    [EndpointDescription("Updates per-session import options, currently whether source files under the Worker incoming root are deleted after each file imports successfully. Options are locked once the session starts committing.")]
    public async Task<ActionResult<ImportSessionUpdateOptionsResponse>> UpdateOptions(
        Guid sessionId,
        [FromBody] ImportSessionUpdateOptionsBody body,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionUpdateOptionsRequest, ImportSessionUpdateOptionsResponse>(
            ImportSessionSubjects.UpdateOptions,
            new ImportSessionUpdateOptionsRequest
            {
                SessionId = sessionId,
                DeleteSourceFiles = body.DeleteSourceFiles
            },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/commit")]
    [Endpoint(EndpointIds.ImportsSessionsCommit)]
    [EndpointSummary("Commit approved local media import items")]
    [EndpointDescription("Moves a reviewed import session into committing state and approves every selected item. Items without yt-dlp, manual, NFO, or info.json metadata automatically use the filename-based placeholder fallback.")]
    public async Task<ActionResult<ImportSessionCommitResponse>> Commit(Guid sessionId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionCommitRequest, ImportSessionCommitResponse>(
            ImportSessionSubjects.Commit,
            new ImportSessionCommitRequest { SessionId = sessionId },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/retry-failed")]
    [Endpoint(EndpointIds.ImportsSessionsRetry)]
    [EndpointSummary("Retry failed local media import items")]
    [EndpointDescription("Returns failed eligible items in an import session to the approved queue and moves the session back into committing state so the dispatcher retries only those failed items.")]
    public async Task<ActionResult<ImportSessionRetryFailedResponse>> RetryFailed(Guid sessionId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionRetryFailedRequest, ImportSessionRetryFailedResponse>(
            ImportSessionSubjects.RetryFailed,
            new ImportSessionRetryFailedRequest { SessionId = sessionId },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/cancel")]
    [Endpoint(EndpointIds.ImportsSessionsCancel)]
    [EndpointSummary("Cancel a local media import session")]
    [EndpointDescription("Marks a non-terminal import session cancelled and excludes items that have not yet started committing. In-flight item flows are allowed to finish or fail normally.")]
    public async Task<ActionResult<ImportSessionCancelResponse>> Cancel(Guid sessionId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ImportSessionCancelRequest, ImportSessionCancelResponse>(
            ImportSessionSubjects.Cancel,
            new ImportSessionCancelRequest { SessionId = sessionId },
            cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpGet("/api/global/imports/incoming/browse")]
    [Endpoint(EndpointIds.ImportsIncomingBrowse)]
    [EndpointSummary("Browse a worker's incoming folder")]
    [EndpointDescription("Lists direct child folders under a safe path within a worker's incoming root. An optional worker tag routes the read-only request to the worker that will perform the scan.")]
    public async Task<ActionResult<BrowseImportIncomingResponse>> BrowseIncoming(
        [FromQuery] string? path,
        [FromQuery] string? workerTag,
        CancellationToken cancellationToken)
    {
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? LocalImportSubjects.BrowseIncomingRequest
            : LocalImportSubjects.BrowseIncomingRequestForTag(workerTag.Trim());
        var response = await SendAsync<BrowseImportIncomingRequest, BrowseImportIncomingResponse>(
            subject,
            new BrowseImportIncomingRequest { SubPath = path },
            cancellationToken);
        if (response is null)
            return BadGateway("No import worker answered the folder browse request.");
        if (!response.Success)
            return Error(response.ErrorCode, response.ErrorMessage);
        return Ok(response);
    }

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import-session request/reply failed on subject {Subject}.", subject);
            return default;
        }
    }

    private ActionResult Error(string? code, string? message) => code switch
    {
        "validation" => BadRequest(new ProblemDetails { Title = message ?? "Invalid import session request.", Status = StatusCodes.Status400BadRequest }),
        "not_found" => NotFound(new ProblemDetails { Title = message ?? "Import session was not found.", Status = StatusCodes.Status404NotFound }),
        _ => StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails { Title = message ?? "Import session service failed.", Status = StatusCodes.Status502BadGateway })
    };

    private ActionResult BadGateway(string? message = null)
        => StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
        {
            Title = message ?? "Import session service unavailable.",
            Status = StatusCodes.Status502BadGateway
        });

    public sealed record ImportSessionEnrichBody(IReadOnlyList<Guid>? ItemIds, ImportSessionYtDlpOptions? Options);

    public sealed record ImportSessionUpdateOptionsBody(bool? DeleteSourceFiles);

    public sealed record ImportSessionMetadataRefreshBody(IReadOnlyList<Guid>? ItemIds);

    public sealed record ImportSessionItemPatchBody
    {
        public string? Title { get; init; }
        public string? Provider { get; init; }
        public string? SourceMediaId { get; init; }
        public string? SourceUrl { get; init; }
    }

    public sealed record ImportSessionItemsBulkBody
    {
        public required ImportSessionBulkAction Action { get; init; }
        public IReadOnlyList<Guid>? ItemIds { get; init; }
        public ImportSessionItemStatus? Status { get; init; }
        public ImportSessionItemMetadataState? MetadataState { get; init; }
        public string? Search { get; init; }
    }

    private static string? ResolveMappingFormat(string fileName, string? contentType)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "text/csv", StringComparison.OrdinalIgnoreCase))
        {
            return "csv";
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return "json";
        }

        return null;
    }
}
