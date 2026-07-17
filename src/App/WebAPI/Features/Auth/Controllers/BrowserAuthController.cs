using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using WebAPI.Auth;

namespace WebAPI.Features.Auth.Controllers;

[ApiController]
[Route("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BrowserAuthController(IConfiguration configuration) : ControllerBase
{
    [HttpGet("login")]
    [AllowAnonymous]
    [EndpointSummary("Start browser sign-in")]
    [EndpointDescription("Validates a same-origin return path and starts the WebAPI-owned OpenID Connect authorization-code flow. In single-user mode, redirects directly to the local destination.")]
    public IActionResult Login([FromQuery] string? returnTo = null, [FromQuery] string? redirectTo = null)
    {
        var destination = LocalReturnPath.Normalize(returnTo ?? redirectTo);
        if (AuthMode.IsSingleUserMode(configuration))
        {
            return LocalRedirect(destination);
        }

        return Challenge(
            new AuthenticationProperties { RedirectUri = destination },
            BffAuthenticationDefaults.OpenIdConnectScheme);
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = BffAuthenticationDefaults.CookieScheme)]
    [EndpointSummary("Sign out the browser session")]
    [EndpointDescription("Revokes the server-side NATS KV authentication ticket and clears the opaque browser session cookie. Cookie-authenticated callers must provide a valid CSRF token.")]
    public async Task<IActionResult> Logout()
    {
        var authentication = await HttpContext.AuthenticateAsync(BffAuthenticationDefaults.CookieScheme);
        string? sessionKey = null;
        authentication.Properties?.Items.TryGetValue(
            BffAuthenticationDefaults.SessionKeyProperty,
            out sessionKey);
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            await HttpContext.SignOutAsync(BffAuthenticationDefaults.CookieScheme);
            return NoContent();
        }

        var ticketStore = HttpContext.RequestServices.GetRequiredService<NatsBffTicketStore>();
        await using var refreshLease = await ticketStore.AcquireRefreshLeaseAsync(
            sessionKey,
            HttpContext.RequestAborted);
        await ticketStore.RemoveAsync(sessionKey, HttpContext.RequestAborted);
        await HttpContext.SignOutAsync(BffAuthenticationDefaults.CookieScheme);
        return NoContent();
    }
}
