using DataBridge.Lite;
using DataBridge.Data;
using Microsoft.AspNetCore.Mvc;
using Shared.Application;
using Shared.Messaging;

namespace FrostStream.Lite.Controllers;

[ApiController]
public sealed class DiscoveryAndImportsController(
    ILiteDiscoveryAndImportIngress ingress,
    IImportSessionRepository imports,
    ICurrentOwner currentOwner) : ControllerBase
{
    [HttpPost("api/playlists/expand")]
    public async Task<IActionResult> ExpandPlaylist([FromBody] LitePlaylistSubmission request, CancellationToken ct)
        => Accepted(await ingress.AcceptPlaylistAsync(request, currentOwner.Subject!, ct));

    [HttpPost("api/creators/scan")]
    public async Task<IActionResult> ScanCreator([FromBody] LiteCreatorScanSubmission request, CancellationToken ct)
        => Accepted(await ingress.AcceptCreatorScanAsync(request, currentOwner.Subject!, ct));

    [HttpPost("api/imports/scan")]
    public async Task<IActionResult> ScanImport([FromBody] LiteImportScanSubmission request, CancellationToken ct)
    {
        var accepted = await ingress.AcceptImportScanAsync(request, ct);
        return Accepted(new { accepted.SessionId, accepted.Receipt });
    }

    [HttpPost("api/imports/{sessionId:guid}/commit")]
    public async Task<IActionResult> CommitImport(Guid sessionId, CancellationToken ct)
        => Accepted(await ingress.CommitImportAsync(sessionId, ct));

    [HttpGet("api/imports/{sessionId:guid}")]
    public async Task<IActionResult> GetImport(Guid sessionId, CancellationToken ct)
        => await imports.GetAsync(sessionId, ct) is { } session ? Ok(session) : NotFound();

    [HttpGet("api/imports/{sessionId:guid}/items")]
    public async Task<IActionResult> ListImportItems(
        Guid sessionId,
        [FromQuery] bool? included,
        [FromQuery] ImportSessionItemStatus? status,
        [FromQuery] ImportSessionItemMetadataState? metadataState,
        [FromQuery] string? search,
        [FromQuery] Guid? afterItemId,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (await imports.GetAsync(sessionId, ct) is null) return NotFound();
        var (items, nextItemId, totalCount) = await imports.ListItemsAsync(new ImportSessionItemsListRequest
        {
            SessionId = sessionId,
            Included = included,
            Status = status,
            MetadataState = metadataState,
            Search = search,
            AfterItemId = afterItemId,
            Limit = limit
        }, ct);
        return Ok(new { items, nextItemId, totalCount });
    }

    [HttpPost("api/imports/{sessionId:guid}/items/bulk")]
    public async Task<IActionResult> BulkImportItems(
        Guid sessionId,
        [FromBody] LiteImportBulkRequest request,
        CancellationToken ct)
    {
        var (affectedCount, session) = await imports.ApplyBulkAsync(new ImportSessionItemsBulkRequest
        {
            SessionId = sessionId,
            Action = request.Action,
            ItemIds = request.ItemIds,
            Status = request.Status,
            MetadataState = request.MetadataState,
            Search = request.Search
        }, ct);
        return session is null ? NotFound() : Ok(new { affectedCount, session });
    }

    public sealed record LiteImportBulkRequest
    {
        public required ImportSessionBulkAction Action { get; init; }
        public IReadOnlyList<Guid>? ItemIds { get; init; }
        public ImportSessionItemStatus? Status { get; init; }
        public ImportSessionItemMetadataState? MetadataState { get; init; }
        public string? Search { get; init; }
    }
}
