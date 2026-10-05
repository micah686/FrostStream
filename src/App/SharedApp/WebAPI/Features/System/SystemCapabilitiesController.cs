using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Deployment;

namespace WebAPI.Features.SystemInfo;

[ApiController]
[Route("api/system")]
public sealed class SystemCapabilitiesController(SystemCapabilities capabilities) : ControllerBase
{
    [HttpGet("capabilities")]
    [AllowAnonymous]
    [EndpointSummary("Get deployment capabilities")]
    [EndpointDescription("Returns the configured integrations, access-management controls, and backup operations available in this deployment. It contains no credentials or internal service addresses.")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ActionResult<SystemCapabilities> GetCapabilities() => Ok(capabilities);
}
