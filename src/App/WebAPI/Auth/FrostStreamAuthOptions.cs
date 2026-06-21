namespace WebAPI.Auth;

public sealed class FrostStreamAuthOptions
{
    public const string SectionName = "Auth";

    public bool SingleUserMode { get; init; }

    public string Authority { get; init; } = "";

    public string Audience { get; init; } = "froststream-api";

    public bool RequireHttpsMetadata { get; init; } = true;

    public bool ExposeOpenApi { get; init; }
}

public sealed class OpenFgaOptions
{
    public const string SectionName = "OpenFga";

    public string Endpoint { get; init; } = "";

    public string StoreId { get; init; } = "";

    public string? AuthorizationModelId { get; init; }

    public string? ApiToken { get; init; }
}
