using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Shared.Auth;

namespace WebAPI.Auth;

public sealed class SingleUserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(AuthConstants.SubjectClaim, AuthConstants.SingleUserSubject),
            new Claim(ClaimTypes.NameIdentifier, AuthConstants.SingleUserSubject),
            new Claim(ClaimTypes.Email, "owner@localhost"),
            new Claim("email", "owner@localhost"),
            new Claim(AuthConstants.PreferredUsernameClaim, "owner"),
            new Claim(AuthConstants.GroupsClaim, "owner"),
            new Claim(AuthConstants.GroupsClaim, "admins"),
            new Claim(ClaimTypes.Role, "owner"),
            new Claim(ClaimTypes.Role, "admins")
        };

        var identity = new ClaimsIdentity(claims, AuthConstants.SingleUserScheme, AuthConstants.PreferredUsernameClaim, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthConstants.SingleUserScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
