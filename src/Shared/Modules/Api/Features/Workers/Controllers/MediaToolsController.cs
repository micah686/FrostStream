using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Deployment;
using Shared.Messaging;
using WebAPI.Auth;
using FrostStream.ApplicationContracts;

namespace WebAPI.Features.Workers.Controllers;

[ApiController]
[Route("api/admin/media-tools")]
public sealed class MediaToolsController(
    IMessageBus bus,
    IConfiguration configuration,
    IServiceProvider services,
    ILogger<MediaToolsController> logger) : ControllerBase
{
    [HttpPost("update")]
    [Endpoint(EndpointIds.MediaToolsUpdate)]
    [EndpointSummary("Update media tools")]
    public async Task<ActionResult<MediaToolsUpdateResponse>> Update(CancellationToken cancellationToken)
    {
        try
        {
            if (DeploymentOptions.FromConfiguration(configuration).Mode == DeploymentMode.Lite)
            {
                var manager = services.GetRequiredService<IMediaToolsUpdater>();
                await manager.UpdateNowAsync(cancellationToken);
                return Ok(new MediaToolsUpdateResponse(true, "Media tools updated."));
            }

            var result = await bus.RequestAsync<MediaToolsUpdateRequest, MediaToolsUpdateResponse>(
                MediaToolsSubjects.Update, new MediaToolsUpdateRequest(), TimeSpan.FromMinutes(5), cancellationToken);
            if (result is null) return StatusCode(503, new { message = "No worker responded to the update request." });
            return result.Success ? Ok(result) : StatusCode(502, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Media tool update request failed.");
            return StatusCode(503, new { message = "Unable to update media tools." });
        }
    }
}
