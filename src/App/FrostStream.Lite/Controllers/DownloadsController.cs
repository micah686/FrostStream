using DataBridge.Lite;
using Microsoft.AspNetCore.Mvc;
using Shared.Application;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/downloads")]
public sealed class DownloadsController(ILiteDownloadIngress ingress, ICurrentOwner currentOwner) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] LiteDownloadSubmission request,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await ingress.AcceptAsync(request, currentOwner.Subject!, cancellationToken);
            return Accepted($"/api/jobs/local/{receipt.WorkId}", receipt);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { errorCode = "invalid_download", errorMessage = exception.Message });
        }
    }

}
