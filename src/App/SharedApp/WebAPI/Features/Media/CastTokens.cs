using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Shared.Auth;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WebAPI.Features.Media;

public static class CastTokenDefaults
{
    public const string Scheme = "CastToken";

    /// <summary>Query parameter carrying the token. A query parameter (not a header) because cast
    /// receivers and &lt;video&gt;/HLS fetches cannot attach custom headers.</summary>
    public const string QueryParameter = "castToken";
}

public static class CastTokenClaims
{
    /// <summary>Marks a principal as a cast-token session scoped to a single media GUID.</summary>
    public const string MediaGuid = "cast_media";
}

public sealed class CastTokenOptions
{
    public const string SectionName = "Cast";

    /// <summary>HMAC secret for cast tokens. When unset, a random per-process secret is generated,
    /// which invalidates outstanding cast tokens on restart.</summary>
    public string? TokenSecret { get; init; }

    public int TokenLifetimeMinutes { get; init; } = 240;
}

public sealed record CastTokenPayload
{
    public required string Subject { get; init; }
    public string? Username { get; init; }
    public string[] Groups { get; init; } = [];
    public required Guid MediaGuid { get; init; }
    public required long ExpiresUnixSeconds { get; init; }
}

/// <summary>
/// Issues and validates short-lived, single-media cast tokens. A token is a signed snapshot of the
/// issuing user's identity (subject + groups) narrowed to one media GUID, so a cast device — which
/// has no session — can fetch the stream while every downstream check (OpenFGA endpoint invoke,
/// watch-time access) still runs against the real user.
/// Format: base64url(JSON payload) + "." + base64url(HMACSHA256(payload)).
/// </summary>
public sealed class CastTokenService
{
    private readonly byte[] _secret;
    private readonly TimeSpan _lifetime;
    private readonly ILogger<CastTokenService> _logger;

    public CastTokenService(IOptions<CastTokenOptions> options, ILogger<CastTokenService> logger)
    {
        _logger = logger;
        _lifetime = TimeSpan.FromMinutes(Math.Max(1, options.Value.TokenLifetimeMinutes));

        if (string.IsNullOrWhiteSpace(options.Value.TokenSecret))
        {
            _secret = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning(
                "Cast:TokenSecret is not configured; using a random per-process secret. Cast tokens will not survive a restart.");
        }
        else
        {
            _secret = System.Text.Encoding.UTF8.GetBytes(options.Value.TokenSecret);
        }
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(ClaimsPrincipal user, Guid mediaGuid)
    {
        var subject = AuthConstants.FindSubject(user)
            ?? throw new InvalidOperationException("Cannot issue a cast token for an unauthenticated principal.");

        var expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
        var payload = new CastTokenPayload
        {
            Subject = subject,
            Username = user.FindFirst(AuthConstants.PreferredUsernameClaim)?.Value,
            Groups = user.FindAll(AuthConstants.GroupsClaim)
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            MediaGuid = mediaGuid,
            ExpiresUnixSeconds = expiresAt.ToUnixTimeSeconds()
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(_secret, payloadBytes);
        return ($"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}", expiresAt);
    }

    public CastTokenPayload? Validate(string token)
    {
        try
        {
            var separator = token.IndexOf('.');
            if (separator <= 0 || separator == token.Length - 1)
                return null;

            var payloadBytes = Base64UrlDecode(token[..separator]);
            var signature = Base64UrlDecode(token[(separator + 1)..]);
            var expected = HMACSHA256.HashData(_secret, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
                return null;

            var payload = JsonSerializer.Deserialize<CastTokenPayload>(payloadBytes);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Subject))
                return null;

            return DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresUnixSeconds) < DateTimeOffset.UtcNow
                ? null
                : payload;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cast token validation failed unexpectedly.");
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

/// <summary>
/// Authenticates requests carrying a valid <c>?castToken=</c>. The resulting principal mirrors the
/// issuing user's subject and groups, plus a <see cref="CastTokenClaims.MediaGuid"/> claim that
/// <see cref="MediaAccessChecker"/> enforces as a single-media scope. Selected by the FrostStream
/// policy scheme only when the query parameter is present.
/// </summary>
public sealed class CastTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CastTokenService castTokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Query[CastTokenDefaults.QueryParameter].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
        {
            return Task.FromResult(AuthenticateResult.Fail("Cast tokens are valid for read-only requests."));
        }

        var payload = castTokenService.Validate(token);
        if (payload is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired cast token."));
        }

        var claims = new List<Claim>
        {
            new(AuthConstants.SubjectClaim, payload.Subject),
            new(ClaimTypes.NameIdentifier, payload.Subject),
            new(CastTokenClaims.MediaGuid, payload.MediaGuid.ToString("D"))
        };

        if (!string.IsNullOrWhiteSpace(payload.Username))
        {
            claims.Add(new Claim(AuthConstants.PreferredUsernameClaim, payload.Username));
        }

        claims.AddRange(payload.Groups.Select(group => new Claim(AuthConstants.GroupsClaim, group)));

        var identity = new ClaimsIdentity(
            claims,
            CastTokenDefaults.Scheme,
            AuthConstants.PreferredUsernameClaim,
            AuthConstants.GroupsClaim);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CastTokenDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
