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

    /// <summary>When true, WebAPI ensures the store, authorization model, and bootstrap tuples exist on startup.</summary>
    public bool AutoProvision { get; init; } = true;

    /// <summary>Store name used when auto-provisioning (or locating) the FrostStream store.</summary>
    public string StoreName { get; init; } = "froststream";

    /// <summary>Comma-separated Authentik subject ids granted the <c>owner</c> relation during bootstrap.</summary>
    public string? BootstrapOwnerSubjects { get; init; }

    /// <summary>Group whose members receive the <c>admin</c> relation during bootstrap.</summary>
    public string BootstrapAdminGroup { get; init; } = "admins";
}
