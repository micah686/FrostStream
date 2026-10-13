using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Deployment;

namespace WebAPI.Features.SystemInfo;

[ApiController]
[Route("api/system")]
public sealed class SystemCapabilitiesController(SystemCapabilities capabilities, FrostStream.ApplicationContracts.IMessageBus bus) : ControllerBase
{
    [HttpGet("capabilities")]
    [AllowAnonymous]
    [EndpointSummary("Get deployment capabilities")]
    [EndpointDescription("Returns the configured integrations, access-management controls, and backup operations available in this deployment. It contains no credentials or internal service addresses.")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ActionResult<SystemCapabilities> GetCapabilities() => Ok(capabilities);
    [HttpGet("integrations")]
    [EndpointSummary("Get integration startup status")]
    [EndpointDescription("Returns search lifecycle and optional live-chat schema initialization status independently of deployment capabilities and application readiness. Disabled integrations do not require a backing service. No credentials or internal addresses are returned.")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<IntegrationStatusSnapshot>> GetIntegrations(CancellationToken ct)
    {
        try
        {
            var status = await bus.RequestAsync<IntegrationStatusRequest, IntegrationStatusSnapshot>(
                IntegrationStatusSubjects.Status, new(), TimeSpan.FromSeconds(3), ct);
            return status is null ? StatusCode(503) : Ok(status);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return StatusCode(503); }
    }
}
