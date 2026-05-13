using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/metadata")]
public sealed class MetadataAdminController(
    IJetStreamPublisher publisher,
    IClock clock,
    ILogger<MetadataAdminController> logger) : ControllerBase
{
    [HttpPost("reindex")]
    public async Task<IActionResult> TriggerReindex(CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var request = BackgroundJobRequestFactory.CreateSearchReindex(
            BackgroundJobRequestFactory.ManualScheduleKey,
            BackgroundJobRequestFactory.ManualSearchReindexTaskType,
            now,
            now);

        try
        {
            await publisher.PublishAsync(
                BackgroundJobSubjects.SearchReindexRequest,
                request,
                request.IdempotencyKey,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing metadata reindex request.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to publish metadata reindex request.");
        }

        return Accepted();
    }
}
