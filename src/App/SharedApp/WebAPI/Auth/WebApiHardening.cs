namespace WebAPI.Auth;

public static class WebApiHardening
{
    public static void ValidateStartup(FrostStreamAuthOptions options, bool singleUserMode, bool isProduction)
    {
        if (isProduction && singleUserMode && !options.AllowSingleUserModeInProduction)
        {
            throw new InvalidOperationException(
                "SINGLE_USER_MODE is not allowed in production. Set Auth:AllowSingleUserModeInProduction=true only for an intentionally isolated deployment.");
        }

        if (singleUserMode)
        {
            return;
        }

        Require(options.Authority, "Auth:Authority");
        Require(options.PublicOrigin, "Auth:PublicOrigin");
        Require(options.ClientId, "Auth:ClientId");
        Require(options.ClientSecret, "Auth:ClientSecret");
        Require(options.Scopes, "Auth:Scopes");

        RequireHttpUri(options.Authority, "Auth:Authority");
        if (!string.IsNullOrWhiteSpace(options.PublicAuthority))
        {
            RequireHttpUri(options.PublicAuthority, "Auth:PublicAuthority");
        }

        if (!Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var publicOrigin) ||
            (publicOrigin.Scheme != Uri.UriSchemeHttp && publicOrigin.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Auth:PublicOrigin must be an absolute HTTP(S) origin.");
        }

        if (publicOrigin.AbsolutePath != "/" || !string.IsNullOrEmpty(publicOrigin.Query) || !string.IsNullOrEmpty(publicOrigin.Fragment))
        {
            throw new InvalidOperationException("Auth:PublicOrigin must contain only scheme, host, and optional port.");
        }

        var scopes = options.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var required in new[] { "openid", "profile", "offline_access" })
        {
            if (!scopes.Contains(required, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Auth:Scopes must include '{required}'.");
            }
        }

        if (options.SessionLifetimeDays is < 1 or > 30)
        {
            throw new InvalidOperationException("Auth:SessionLifetimeDays must be between 1 and 30 days.");
        }

        if (options.RefreshSkewSeconds is < 15 or > 600)
        {
            throw new InvalidOperationException("Auth:RefreshSkewSeconds must be between 15 and 600 seconds.");
        }

        if (isProduction)
        {
            Require(options.DataProtectionKeysPath, "Auth:DataProtectionKeysPath");
            RequireHttps(publicOrigin, "Auth:PublicOrigin");

            if (!options.SecureCookies)
            {
                throw new InvalidOperationException("Auth:SecureCookies must be enabled in production.");
            }

            var browserAuthority = new Uri(
                string.IsNullOrWhiteSpace(options.PublicAuthority) ? options.Authority : options.PublicAuthority,
                UriKind.Absolute);
            RequireHttps(browserAuthority, "Auth:PublicAuthority");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} must be configured when SINGLE_USER_MODE is not enabled.");
        }
    }

    private static void RequireHttps(Uri uri, string name)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{name} must use HTTPS in production.");
        }
    }

    private static void RequireHttpUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{name} must be an absolute HTTP(S) URI.");
        }
    }
}
