using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Shared.Auth;

namespace WebAPI.Auth;

public static class MediaProcessorAuthenticationDefaults
{
    public const string Scheme = "MediaProcessor";
    public const string ApiKeyHeader = "X-FrostStream-MediaProcessor-Key";
}

public sealed class MediaProcessorAuthOptions
{
    public string? ApiKey { get; init; }
}

public sealed class MediaProcessorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    IOptions<MediaProcessorAuthOptions> mediaProcessorOptions,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = mediaProcessorOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("MediaProcessor API key is not configured."));
        }

        var provided = Request.Headers[MediaProcessorAuthenticationDefaults.ApiKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(provided) || !FixedEquals(provided, expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid MediaProcessor API key."));
        }

        var claims = new[]
        {
            new Claim(AuthConstants.SubjectClaim, "mediaprocessor"),
            new Claim(ClaimTypes.NameIdentifier, "mediaprocessor"),
            new Claim(ClaimTypes.Name, "mediaprocessor")
        };
        var identity = new ClaimsIdentity(claims, MediaProcessorAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, MediaProcessorAuthenticationDefaults.Scheme)));
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
