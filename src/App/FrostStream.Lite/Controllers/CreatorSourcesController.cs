using DataBridge.Data;
using Microsoft.AspNetCore.Mvc;
using Shared.Application;
using Shared.Database;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/creators")]
public sealed class CreatorSourcesController(
    ICreatorDiscoveryRepository repository,
    ICurrentOwner currentOwner) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok(await repository.ListSourcesAsync(cancellationToken));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
        => await repository.GetSourceAsync(id, cancellationToken) is { } source ? Ok(source) : NotFound();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LiteCreatorSourceRequest request, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } error) return BadRequest(error);
        var created = await repository.CreateSourceAsync(ToEntity(request, currentOwner.Subject!), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Source.Id }, created);
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] LiteCreatorSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } error) return BadRequest(error);
        var entity = ToEntity(request, currentOwner.Subject!);
        entity.Id = id;
        return await repository.UpdateSourceAsync(entity, cancellationToken) is { } updated ? Ok(updated) : NotFound();
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
        => await repository.DeleteSourceAsync(id, cancellationToken) ? NoContent() : NotFound();

    private static CreatorSourceEntity ToEntity(LiteCreatorSourceRequest request, string owner) => new()
    {
        SourceUrl = request.SourceUrl,
        ConfigSetOwnerSubject = string.IsNullOrWhiteSpace(request.ConfigSetKey) ? null : owner,
        ConfigSetKey = request.ConfigSetKey,
        ScanEnabled = request.ScanEnabled,
        IncrementalPageSize = request.IncrementalPageSize,
        ConsecutiveKnownThreshold = request.ConsecutiveKnownThreshold,
        FullRescanIntervalDays = request.FullRescanIntervalDays,
        UpdateCheckIntervalHours = request.UpdateCheckIntervalHours,
        MetadataRefreshWindow = request.MetadataRefreshWindow
    };

    private static string? Validate(LiteCreatorSourceRequest request)
    {
        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "SourceUrl must be an absolute HTTP(S) URL.";
        if (request.IncrementalPageSize is < 1 or > 500) return "IncrementalPageSize must be between 1 and 500.";
        if (request.ConsecutiveKnownThreshold is < 1 or > 500) return "ConsecutiveKnownThreshold must be between 1 and 500.";
        if (request.FullRescanIntervalDays is < 1 or > 365) return "FullRescanIntervalDays must be between 1 and 365.";
        if (request.UpdateCheckIntervalHours is < 1 or > 168) return "UpdateCheckIntervalHours must be between 1 and 168.";
        if (request.MetadataRefreshWindow is < 1 or > 500) return "MetadataRefreshWindow must be between 1 and 500.";
        return null;
    }

    public sealed record LiteCreatorSourceRequest
    {
        public required string SourceUrl { get; init; }
        public string? ConfigSetKey { get; init; }
        public bool ScanEnabled { get; init; } = true;
        public int IncrementalPageSize { get; init; } = 50;
        public int ConsecutiveKnownThreshold { get; init; } = 25;
        public int FullRescanIntervalDays { get; init; } = 30;
        public int UpdateCheckIntervalHours { get; init; } = 6;
        public int MetadataRefreshWindow { get; init; } = 25;
    }
}
