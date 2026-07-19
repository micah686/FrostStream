using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using WebAPI.Auth;

namespace WebAPI.Features.Auth.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    IConfiguration configuration,
    ISessionSynchronizationService sessionSynchronization,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("config")]
    [AllowAnonymous]
    [EndpointSummary("Get authentication configuration")]
    [EndpointDescription("Returns only the active FrostStream authentication mode. The endpoint intentionally exposes no identity-provider authority, audience, client secret, token endpoint, or refresh details.")]
    public ActionResult<AuthConfigResponse> GetConfig()
    {
        var singleUserMode = AuthMode.IsSingleUserMode(configuration);
        return Ok(new AuthConfigResponse
        {
            Mode = singleUserMode ? "single-user" : "multi-user"
        });
    }

    [HttpGet("me")]
    [Authorize(Policy = AuthPolicies.Authenticated)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [EndpointSummary("Get the current browser or API session")]
    [EndpointDescription("Returns the authenticated user's non-secret profile, groups, authentication mode, and session expiration. OAuth tokens and internal identity-provider details are never returned.")]
    public ActionResult<AuthMeResponse> GetMe()
    {
        var subject = AuthConstants.FindSubject(User);
        if (subject is null)
        {
            return Unauthorized();
        }

        var groups = User.FindAll(AuthConstants.GroupsClaim)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var name = FirstNonBlank(
            User.FindFirst(AuthConstants.PreferredUsernameClaim)?.Value,
            User.FindFirst("name")?.Value,
            User.Identity?.Name,
            subject) ?? subject;
        var expiresAt = DateTimeOffset.TryParse(
            HttpContext.Features.Get<Microsoft.AspNetCore.Authentication.IAuthenticateResultFeature>()?
                .AuthenticateResult?.Properties?.GetTokenValue("expires_at"),
            out var parsedExpiration)
            ? parsedExpiration
            : (DateTimeOffset?)null;

        return Ok(new AuthMeResponse
        {
            Mode = AuthMode.IsSingleUserMode(configuration) ? "single-user" : "multi-user",
            Authenticated = true,
            Profile = new AuthProfileResponse
            {
                Subject = subject,
                Name = name,
                Username = User.FindFirst(AuthConstants.PreferredUsernameClaim)?.Value,
                Email = User.FindFirst("email")?.Value,
                Groups = groups,
                Initials = Initials(name)
            },
            ExpiresAt = expiresAt
        });
    }

    [HttpGet("csrf")]
    [Authorize(Policy = AuthPolicies.Authenticated)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [EndpointSummary("Issue a browser CSRF request token")]
    [EndpointDescription("Creates or refreshes the same-origin antiforgery cookie and returns the request token that browser clients must send in the X-CSRF-TOKEN header on unsafe cookie-authenticated requests.")]
    public ActionResult<AuthCsrfResponse> GetCsrf()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AuthCsrfResponse { Token = tokens.RequestToken! });
    }

    /// <summary>
    /// Called by the SvelteKit BFF after a successful login/refresh. Upserts the local FrostStream
    /// user from the validated token and reconciles the caller's Authentik groups into OpenFGA
    /// membership tuples so authorization reflects the current group set.
    /// </summary>
    [HttpPost("session")]
    [Authorize(Policy = AuthPolicies.Authenticated)]
    [EndpointSummary("Synchronize the authenticated session")]
    [EndpointDescription("Upserts the local user record keyed by the Authentik subject and refreshes the user's OpenFGA group membership tuples. Intended to be called server-side by the frontend BFF immediately after the OIDC code exchange and on token refresh.")]
    public async Task<ActionResult<AuthSessionResponse>> SyncSession(CancellationToken cancellationToken)
    {
        var result = await sessionSynchronization.SynchronizeAsync(User, cancellationToken);
        if (!result.Success && string.IsNullOrEmpty(result.Subject))
        {
            return Unauthorized();
        }

        if (!result.Success)
        {
            return StatusCode(
                result.ServiceUnavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status502BadGateway,
                result.ErrorMessage);
        }

        return Ok(new AuthSessionResponse
        {
            UserId = result.UserId,
            Subject = result.Subject,
            DisplayName = result.DisplayName,
            Groups = result.Groups
        });
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => string.Concat(words[0][..1], words[^1][..1]).ToUpperInvariant()
        };
    }
}

public sealed record AuthConfigResponse
{
    public required string Mode { get; init; }

}

public sealed record AuthSessionResponse
{
    public required Guid UserId { get; init; }

    public required string Subject { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyList<string> Groups { get; init; }
}

public sealed record AuthMeResponse
{
    public required string Mode { get; init; }
    public required bool Authenticated { get; init; }
    public required AuthProfileResponse Profile { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record AuthProfileResponse
{
    public required string Subject { get; init; }
    public required string Name { get; init; }
    public string? Username { get; init; }
    public string? Email { get; init; }
    public required IReadOnlyList<string> Groups { get; init; }
    public required string Initials { get; init; }
}

public sealed record AuthCsrfResponse
{
    public required string Token { get; init; }
}
