namespace AppHost;

internal sealed record AppHostHardeningOptions(
    bool SingleUserMode,
    bool IsProduction,
    bool EnableHttps,
    bool RequireHttpsMetadata,
    bool EnableFgaAuthenticatedEndpoints,
    string OpenBaoImageTag,
    string OpenFgaImageTag,
    string OpenBaoToken,
    string TypesenseApiKey,
    string OpenFgaApiToken);

internal static class AppHostHardening
{
    private const string DevOpenBaoToken = "froststream-dev-root";
    private const string DevTypesenseApiKey = "froststream-dev-key";
    private const string DevOpenFgaApiToken = "froststream-dev-openfga-token-change-me";
    private const string DevAuthentikClientSecret = "froststream-dev-client-secret";
    private const string DevAuthentikBootstrapPassword = "froststream-dev-admin";

    public static AppHostHardeningOptions Read(bool singleUserMode)
    {
        var isProduction = IsTruthy(Environment.GetEnvironmentVariable("FROSTSTREAM_PRODUCTION")) ||
            string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase);

        var enableHttps = IsTruthy(Environment.GetEnvironmentVariable("ENABLE_HTTPS"));
        var requireHttpsMetadata = ReadBool("AUTH_REQUIRE_HTTPS_METADATA", enableHttps);
        var enableFgaAuthenticatedEndpoints = !singleUserMode &&
            ReadBool("ENABLE_FGA_AUTHENTICATED_ENDPOINTS", isProduction);

        return new AppHostHardeningOptions(
            singleUserMode,
            isProduction,
            enableHttps,
            requireHttpsMetadata,
            enableFgaAuthenticatedEndpoints,
            OpenBaoImageTag: Environment.GetEnvironmentVariable("OPENBAO_IMAGE_TAG") ?? "2.5.5",
            OpenFgaImageTag: Environment.GetEnvironmentVariable("OPENFGA_IMAGE_TAG") ?? "v1.18.0",
            OpenBaoToken: Environment.GetEnvironmentVariable("OPENBAO_TOKEN") ?? DevOpenBaoToken,
            TypesenseApiKey: Environment.GetEnvironmentVariable("TYPESENSE_API_KEY") ?? DevTypesenseApiKey,
            OpenFgaApiToken: Environment.GetEnvironmentVariable("OPENFGA_API_TOKEN") ?? DevOpenFgaApiToken);
    }

    public static void Validate(AppHostHardeningOptions options)
    {
        if (!options.IsProduction)
        {
            return;
        }

        var errors = new List<string>();

        if (options.SingleUserMode && !IsTruthy(Environment.GetEnvironmentVariable("AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION")))
        {
            errors.Add("SINGLE_USER_MODE is not allowed when FROSTSTREAM_PRODUCTION=true unless AUTH_ALLOW_SINGLE_USER_MODE_IN_PRODUCTION=true.");
        }

        if (!options.EnableHttps)
        {
            errors.Add("ENABLE_HTTPS=true is required when FROSTSTREAM_PRODUCTION=true.");
        }

        if (!options.SingleUserMode &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTHENTIK_AUTHORITY")))
        {
            errors.Add("AUTHENTIK_AUTHORITY must be set to the external HTTPS issuer URL in production.");
        }

        if (!options.SingleUserMode &&
            !IsHttps(Environment.GetEnvironmentVariable("AUTHENTIK_AUTHORITY")))
        {
            errors.Add("AUTHENTIK_AUTHORITY must use https:// in production.");
        }

        RequireStrongSecret(errors, "POSTGRES_PASSWORD", Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"), "postgres", 16);
        RequireStrongSecret(errors, "OPENBAO_TOKEN", Environment.GetEnvironmentVariable("OPENBAO_TOKEN"), DevOpenBaoToken, 32);
        RequireStrongSecret(errors, "TYPESENSE_API_KEY", Environment.GetEnvironmentVariable("TYPESENSE_API_KEY"), DevTypesenseApiKey, 32);

        if (!options.SingleUserMode)
        {
            RequireStrongSecret(errors, "AUTHENTIK_SECRET_KEY", Environment.GetEnvironmentVariable("AUTHENTIK_SECRET_KEY"), null, 32);
            RequireStrongSecret(errors, "AUTHENTIK_CLIENT_SECRET", Environment.GetEnvironmentVariable("AUTHENTIK_CLIENT_SECRET"), DevAuthentikClientSecret, 32);
            RequireStrongSecret(errors, "AUTHENTIK_BOOTSTRAP_PASSWORD", Environment.GetEnvironmentVariable("AUTHENTIK_BOOTSTRAP_PASSWORD"), DevAuthentikBootstrapPassword, 16);

            if (options.EnableFgaAuthenticatedEndpoints)
            {
                RequireStrongSecret(errors, "OPENFGA_API_TOKEN", Environment.GetEnvironmentVariable("OPENFGA_API_TOKEN"), DevOpenFgaApiToken, 32);
            }

            if (IsFalsey(Environment.GetEnvironmentVariable("OPENFGA_AUTO_PROVISION")) &&
                (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENFGA_STORE_ID")) ||
                 string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENFGA_AUTHORIZATION_MODEL_ID"))))
            {
                errors.Add("OPENFGA_STORE_ID and OPENFGA_AUTHORIZATION_MODEL_ID are required when OPENFGA_AUTO_PROVISION=false.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Production hardening validation failed:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => "- " + error)));
        }
    }

    private static void RequireStrongSecret(
        List<string> errors,
        string name,
        string? value,
        string? forbiddenValue,
        int minimumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} must be set.");
            return;
        }

        if (value.Length < minimumLength)
        {
            errors.Add($"{name} must be at least {minimumLength} characters.");
        }

        if (!string.IsNullOrEmpty(forbiddenValue) && string.Equals(value, forbiddenValue, StringComparison.Ordinal))
        {
            errors.Add($"{name} must not use the development default.");
        }
    }

    private static bool ReadBool(string name, bool defaultValue)
        => Environment.GetEnvironmentVariable(name) is { } value
            ? IsTruthy(value)
            : defaultValue;

    private static bool IsHttps(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string? value)
        => value is not null &&
           (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));

    private static bool IsFalsey(string? value)
        => value is not null &&
           (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "off", StringComparison.OrdinalIgnoreCase));
}
