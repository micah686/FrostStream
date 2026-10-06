using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Shared.Auth;

namespace WebAPI.Auth;

public sealed record BffSessionValidationResult(
    bool IsValid,
    ClaimsPrincipal? Principal = null,
    AuthenticationProperties? Properties = null,
    bool Revoke = false);

public sealed class BffSessionRefreshService(
    NatsBffTicketStore ticketStore,
    IAccessTokenValidator tokenValidator,
    ISessionSynchronizationService sessionSynchronization,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<FrostStreamAuthOptions> authOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<BffSessionRefreshService> logger)
{
    private readonly FrostStreamAuthOptions _authOptions = authOptions.Value;

    public async Task<BffSessionValidationResult> ValidateAsync(
        string sessionKey,
        AuthenticationTicket currentTicket,
        CancellationToken cancellationToken)
    {
        var expiresAt = ReadExpiration(currentTicket.Properties);
        var refreshAt = DateTimeOffset.UtcNow.AddSeconds(_authOptions.RefreshSkewSeconds);
        if (expiresAt > refreshAt)
        {
            return await ValidateTicketAsync(currentTicket, cancellationToken);
        }

        var refreshToken = currentTicket.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return expiresAt > DateTimeOffset.UtcNow
                ? await ValidateTicketAsync(currentTicket, cancellationToken)
                : new(false, Revoke: true);
        }

        try
        {
            await using var refreshLease = await ticketStore.AcquireRefreshLeaseAsync(sessionKey, cancellationToken);

            // Another request may have refreshed while this request waited for the lease.
            var latest = await ticketStore.RetrieveAsync(sessionKey, cancellationToken);
            if (latest is null)
            {
                return new(false, Revoke: true);
            }

            var latestExpiration = ReadExpiration(latest.Properties);
            if (latestExpiration > DateTimeOffset.UtcNow.AddSeconds(_authOptions.RefreshSkewSeconds))
            {
                return await ValidateTicketAsync(latest, cancellationToken);
            }

            var latestRefreshToken = latest.Properties.GetTokenValue("refresh_token");
            if (string.IsNullOrWhiteSpace(latestRefreshToken))
            {
                return latestExpiration > DateTimeOffset.UtcNow
                    ? await ValidateTicketAsync(latest, cancellationToken)
                    : new(false, Revoke: true);
            }

            var refreshed = await RefreshTokensAsync(latestRefreshToken, cancellationToken);
            var principal = await tokenValidator.ValidateAsync(refreshed.AccessToken, cancellationToken);
            UpdateTokens(latest.Properties, refreshed, latestRefreshToken);
            var renewed = new AuthenticationTicket(
                principal,
                latest.Properties,
                BffAuthenticationDefaults.CookieScheme);
            await ticketStore.RenewAsync(sessionKey, renewed, cancellationToken);

            var sync = await sessionSynchronization.SynchronizeAsync(principal, cancellationToken);
            if (!sync.Success)
            {
                logger.LogWarning("Session synchronization after refresh failed for subject {Subject}: {Error}",
                    AuthConstants.FindSubject(principal), sync.ErrorMessage);
            }

            return new(true, principal, latest.Properties);
        }
        catch (TerminalRefreshException ex)
        {
            logger.LogInformation("Revoking browser session after terminal OAuth refresh failure {Error}.", ex.Error);
            return new(false, Revoke: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transient browser-session refresh failure.");
            if (expiresAt > DateTimeOffset.UtcNow)
            {
                return await ValidateTicketAsync(currentTicket, cancellationToken);
            }

            return new(false);
        }
    }

    private async Task<BffSessionValidationResult> ValidateTicketAsync(
        AuthenticationTicket ticket,
        CancellationToken cancellationToken)
    {
        var accessToken = ticket.Properties.GetTokenValue("access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new(false, Revoke: true);
        }

        try
        {
            var principal = await tokenValidator.ValidateAsync(accessToken, cancellationToken);
            return new(true, principal, ticket.Properties);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Revoking a browser session whose stored access token is invalid.");
            return new(false, Revoke: true);
        }
    }

    private async Task<RefreshedTokens> RefreshTokensAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var options = oidcOptions.Get(BffAuthenticationDefaults.OpenIdConnectScheme);
        var configuration = options.ConfigurationManager is null
            ? options.Configuration
            : await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
        var tokenEndpoint = configuration?.TokenEndpoint;
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            throw new InvalidOperationException("OIDC discovery did not provide a token endpoint.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _authOptions.ClientId,
                ["client_secret"] = _authOptions.ClientSecret,
                ["refresh_token"] = refreshToken
            })
        };
        using var response = await httpClientFactory.CreateClient(BffAuthenticationDefaults.HttpClientName)
            .SendAsync(request, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = json.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString() ?? "unknown_error"
                : "unknown_error";
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                error is "invalid_grant" or "invalid_client" or "unauthorized_client")
            {
                throw new TerminalRefreshException(error);
            }

            throw new HttpRequestException($"OIDC token refresh failed with status {(int)response.StatusCode}.");
        }

        var accessToken = json.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("OIDC refresh response did not include an access token.");
        }

        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiresElement) &&
                        expiresElement.TryGetInt32(out var seconds)
            ? seconds
            : 3600;
        var rotatedRefreshToken = json.RootElement.TryGetProperty("refresh_token", out var refreshElement)
            ? refreshElement.GetString()
            : null;
        var idToken = json.RootElement.TryGetProperty("id_token", out var idElement)
            ? idElement.GetString()
            : null;
        return new(accessToken, rotatedRefreshToken, idToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static void UpdateTokens(
        AuthenticationProperties properties,
        RefreshedTokens refreshed,
        string previousRefreshToken)
    {
        var tokens = properties.GetTokens().ToDictionary(token => token.Name, token => token.Value, StringComparer.Ordinal);
        tokens["access_token"] = refreshed.AccessToken;
        tokens["refresh_token"] = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
            ? previousRefreshToken
            : refreshed.RefreshToken;
        tokens["expires_at"] = refreshed.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(refreshed.IdToken))
        {
            tokens["id_token"] = refreshed.IdToken;
        }

        properties.StoreTokens(tokens.Select(pair => new AuthenticationToken
        {
            Name = pair.Key,
            Value = pair.Value
        }));
    }

    private static DateTimeOffset ReadExpiration(AuthenticationProperties properties)
    {
        var value = properties.GetTokenValue("expires_at");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAt)
            ? expiresAt
            : DateTimeOffset.MinValue;
    }

    private sealed record RefreshedTokens(
        string AccessToken,
        string? RefreshToken,
        string? IdToken,
        DateTimeOffset ExpiresAt);

    private sealed class TerminalRefreshException(string error) : Exception(error)
    {
        public string Error { get; } = error;
    }
}
