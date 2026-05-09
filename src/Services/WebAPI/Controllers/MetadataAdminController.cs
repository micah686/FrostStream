using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/metadata")]
public sealed class MetadataAdminController(
    IMessageBus messageBus,
    ILogger<MetadataAdminController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    [HttpPost("reindex")]
    public async Task<IActionResult> TriggerReindex(CancellationToken cancellationToken)
    {
        MetadataSyncRebuildResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<MetadataSyncRebuildRequestMessage, MetadataSyncRebuildResponseMessage>(
                MetadataSyncSubjects.SyncRebuild,
                new MetadataSyncRebuildRequestMessage(),
                QueryTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing metadata reindex request.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        }

        if (response is null || !response.Accepted)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, response?.ErrorMessage ?? "DataBridge did not accept the rebuild request.");

        return Accepted();
    }
}
