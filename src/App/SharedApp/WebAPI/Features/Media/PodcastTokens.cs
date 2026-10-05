using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Shared.Auth;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WebAPI.Features.Media;

public static class PodcastTokenDefaults
{
    public const string Scheme = "PodcastToken";
    public const string QueryParameter = "podcastToken";
}

public sealed class PodcastTokenOptions
{
    public const string SectionName = "Podcast";
    public int TokenLifetimeDays { get; init; } = 3650;
}

public sealed record PodcastTokenPayload
{
    public required string Subject { get; init; }
    public string? Username { get; init; }
    public string[] Groups { get; init; } = [];
    public required long AccountId { get; init; }
}

public sealed class PodcastTokenService
{
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;

    public PodcastTokenService(
        IOptions<PodcastTokenOptions> options,
        IDataProtectionProvider dataProtectionProvider)
    {
        _lifetime = TimeSpan.FromDays(Math.Max(1, options.Value.TokenLifetimeDays));
        _protector = dataProtectionProvider
            .CreateProtector("FrostStream.PodcastToken.v1")
            .ToTimeLimitedDataProtector();
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(ClaimsPrincipal user, long accountId)
    {
        var subject = AuthConstants.FindSubject(user)
            ?? throw new InvalidOperationException("Cannot issue a podcast token for an unauthenticated principal.");
        var expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
        var payload = new PodcastTokenPayload
        {
            Subject = subject,
            Username = user.FindFirst(AuthConstants.PreferredUsernameClaim)?.Value,
            Groups = user.FindAll(AuthConstants.GroupsClaim)
                .Select(x => x.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            AccountId = accountId
        };

        return (_protector.Protect(JsonSerializer.Serialize(payload), _lifetime), expiresAt);
    }

    public PodcastTokenPayload? Validate(string token)
    {
        try
        {
            var json = _protector.Unprotect(token, out _);
            var payload = JsonSerializer.Deserialize<PodcastTokenPayload>(json);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Subject) || payload.AccountId <= 0)
                return null;
            return payload;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

public sealed class PodcastTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PodcastTokenService tokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Query[PodcastTokenDefaults.QueryParameter].ToString();
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
            return Task.FromResult(AuthenticateResult.Fail("Podcast tokens are valid for read-only requests."));

        var payload = tokenService.Validate(token);
        if (payload is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired podcast token."));

        if (!long.TryParse(Request.RouteValues["accountId"]?.ToString(), out var routeAccountId) ||
            routeAccountId != payload.AccountId ||
            !Request.Path.StartsWithSegments($"/api/media/channels/{routeAccountId}/audio"))
        {
            return Task.FromResult(AuthenticateResult.Fail("The podcast token is not valid for this route."));
        }

        var claims = new List<Claim>
        {
            new(AuthConstants.SubjectClaim, payload.Subject),
            new(ClaimTypes.NameIdentifier, payload.Subject)
        };
        if (!string.IsNullOrWhiteSpace(payload.Username))
            claims.Add(new Claim(AuthConstants.PreferredUsernameClaim, payload.Username));
        claims.AddRange(payload.Groups.Select(x => new Claim(AuthConstants.GroupsClaim, x)));

        var identity = new ClaimsIdentity(
            claims,
            PodcastTokenDefaults.Scheme,
            AuthConstants.PreferredUsernameClaim,
            AuthConstants.GroupsClaim);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), PodcastTokenDefaults.Scheme)));
    }
}
