using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Shared.Auth;
using WebAPI.Auth;

namespace WebAPI.Features.Auth.Controllers;

[ApiController]
[Route("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BrowserAuthController(
    IConfiguration configuration,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<FrostStreamAuthOptions> authOptions) : ControllerBase
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
        var providerLogoutUrl = await BuildProviderLogoutUrlAsync(authentication.Properties);
        string? sessionKey = null;
        authentication.Properties?.Items.TryGetValue(
            BffAuthenticationDefaults.SessionKeyProperty,
            out sessionKey);
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            await HttpContext.SignOutAsync(BffAuthenticationDefaults.CookieScheme);
            ClearBrowserCookies();
            return LogoutResult(providerLogoutUrl);
        }

        var ticketStore = HttpContext.RequestServices.GetRequiredService<NatsBffTicketStore>();
        await using var refreshLease = await ticketStore.AcquireRefreshLeaseAsync(
            sessionKey,
            HttpContext.RequestAborted);
        await ticketStore.RemoveAsync(sessionKey, HttpContext.RequestAborted);
        await HttpContext.SignOutAsync(BffAuthenticationDefaults.CookieScheme);
        ClearBrowserCookies();
        return LogoutResult(providerLogoutUrl);
    }

    private IActionResult LogoutResult(string? providerLogoutUrl)
        => string.IsNullOrWhiteSpace(providerLogoutUrl)
            ? NoContent()
            : Ok(new { providerLogoutUrl });

    private async Task<string?> BuildProviderLogoutUrlAsync(AuthenticationProperties? properties)
    {
        if (AuthMode.IsSingleUserMode(configuration))
        {
            return null;
        }

        try
        {
            var options = oidcOptions.Get(BffAuthenticationDefaults.OpenIdConnectScheme);
            var discovery = options.ConfigurationManager is null
                ? options.Configuration
                : await options.ConfigurationManager.GetConfigurationAsync(HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(discovery?.EndSessionEndpoint))
            {
                return null;
            }

            var configured = authOptions.Value;
            var endpoint = RewriteBrowserEndpoint(
                discovery.EndSessionEndpoint,
                string.IsNullOrWhiteSpace(configured.PublicAuthority) ? configured.Authority : configured.PublicAuthority);
            var parameters = new Dictionary<string, string?>
            {
                ["post_logout_redirect_uri"] = $"{configured.PublicOrigin.TrimEnd('/')}/",
                ["id_token_hint"] = properties?.GetTokenValue("id_token")
            };
            return QueryHelpers.AddQueryString(endpoint, parameters!);
        }
        catch
        {
            // Local logout must remain available if provider discovery is temporarily unavailable.
            return null;
        }
    }

    private void ClearBrowserCookies()
    {
        Response.Cookies.Delete(BffAuthenticationDefaults.CookieName, new CookieOptions { Path = "/" });
        Response.Cookies.Delete(BffAuthenticationDefaults.AntiforgeryCookieName, new CookieOptions { Path = "/" });
    }

    private static string RewriteBrowserEndpoint(string endpoint, string publicAuthority)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var source) ||
            !Uri.TryCreate(publicAuthority, UriKind.Absolute, out var browser))
        {
            return endpoint;
        }

        return new UriBuilder(source)
        {
            Scheme = browser.Scheme,
            Host = browser.Host,
            Port = browser.IsDefaultPort ? -1 : browser.Port
        }.Uri.ToString();
    }
}
