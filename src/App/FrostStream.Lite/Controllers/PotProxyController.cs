using DataBridge.Lite;
using Microsoft.AspNetCore.Mvc;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/internal/pot")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class PotProxyController(ILitePotProvider provider) : ControllerBase
{
    [AcceptVerbs("GET", "POST")]
    [Route("{**path}")]
    public async Task<IActionResult> Forward(string? path, CancellationToken cancellationToken)
    {
        if (!provider.Enabled) return NotFound();
        var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
        var response = await provider.ForwardAsync(
            new HttpMethod(Request.Method), $"{path}{query}",
            Request.ContentLength is > 0 ? Request.Body : null,
            Request.ContentType, cancellationToken);
        Response.StatusCode = (int)response.StatusCode;
        return File(response.Body, response.ContentType ?? "application/octet-stream");
    }
}
