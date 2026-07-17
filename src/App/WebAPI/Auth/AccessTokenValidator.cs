using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace WebAPI.Auth;

public interface IAccessTokenValidator
{
    Task<ClaimsPrincipal> ValidateAsync(string accessToken, CancellationToken cancellationToken);
}

/// <summary>Validates BFF access tokens with the exact configuration used by JwtBearer.</summary>
public sealed class AccessTokenValidator(IOptionsMonitor<JwtBearerOptions> optionsMonitor)
    : IAccessTokenValidator
{
    public async Task<ClaimsPrincipal> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        var options = optionsMonitor.Get(JwtBearerDefaults.AuthenticationScheme);
        var parameters = options.TokenValidationParameters.Clone();

        if (options.ConfigurationManager is not null)
        {
            var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(parameters.ValidIssuer))
            {
                parameters.ValidIssuer = configuration.Issuer;
            }

            parameters.IssuerSigningKeys = configuration.SigningKeys;
        }

        foreach (var handler in options.TokenHandlers)
        {
            var result = await handler.ValidateTokenAsync(accessToken, parameters);
            if (result.IsValid && result.ClaimsIdentity is not null)
            {
                return new ClaimsPrincipal(result.ClaimsIdentity);
            }
        }

        throw new SecurityTokenValidationException("The OIDC access token failed API validation.");
    }
}
