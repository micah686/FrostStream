using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using WebAPI.Auth;

namespace WebAPI.Features.Auth.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IConfiguration configuration) : ControllerBase
{
    [HttpGet("config")]
    [AllowAnonymous]
    [EndpointSummary("Get authentication configuration")]
    [EndpointDescription("Returns the active FrostStream authentication mode and non-secret client configuration needed by the frontend. The endpoint is intentionally anonymous so the UI can decide whether to use single-user mode or start the external OIDC login flow before calling protected API routes.")]
    public ActionResult<AuthConfigResponse> GetConfig()
    {
        var singleUserMode = AuthMode.IsSingleUserMode(configuration);
        return Ok(new AuthConfigResponse
        {
            Mode = singleUserMode ? "single-user" : "multi-user",
            Authority = singleUserMode ? null : configuration["Auth:Authority"],
            Audience = configuration["Auth:Audience"] ?? "froststream-api"
        });
    }
}

public sealed record AuthConfigResponse
{
    public required string Mode { get; init; }

    public string? Authority { get; init; }

    public required string Audience { get; init; }
}
