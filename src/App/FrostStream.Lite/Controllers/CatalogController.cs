using DataBridge.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/metadata")]
public sealed class CatalogController(IMetadataReadService metadata) : ControllerBase
{
    [HttpGet("{mediaGuid:guid}")]
    public async Task<IActionResult> Get(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var item = await metadata.GetDetailAsync(mediaGuid, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpGet("random")]
    public async Task<IActionResult> Random(
        [FromQuery] Guid? exclude,
        CancellationToken cancellationToken)
    {
        var mediaGuid = await metadata.GetRandomMediaGuidAsync(exclude, cancellationToken);
        return mediaGuid is null ? NotFound() : Ok(new { mediaGuid });
    }
}
