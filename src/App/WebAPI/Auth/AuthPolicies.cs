using Microsoft.AspNetCore.Authorization;
using Shared.Auth;

namespace WebAPI.Auth;

/// <summary>
/// Named authorization policies applied across FrostStream controllers. Each policy maps to a
/// coarse OpenFGA relation on <see cref="AuthConstants.SystemObject"/>. Resource-scoped checks
/// (per download/playlist/cookie owner) are layered on top of these in later auth work.
/// </summary>
public static class AuthPolicies
{
    /// <summary>Viewer/member-level read access to FrostStream. Mirrors the global fallback policy.</summary>
    public const string SystemAccess = "SystemAccess";

    /// <summary>Admin-level management access (storage, schedules, presets, creator sources, reindex).</summary>
    public const string SystemManage = "SystemManage";

    /// <summary>
    /// Authentication only, with no OpenFGA check. Used by the session-sync endpoint, which is what
    /// bootstraps a user's access tuples — requiring <see cref="SystemAccess"/> there would 403 every
    /// first-time login before any tuples exist.
    /// </summary>
    public const string Authenticated = "Authenticated";

    public static void AddFrostStreamPolicies(AuthorizationOptions options)
    {
        var authenticated = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        var access = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new FrostStreamPermissionRequirement(AuthConstants.AccessRelation, AuthConstants.SystemObject))
            .Build();

        var manage = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new FrostStreamPermissionRequirement(AuthConstants.ManageRelation, AuthConstants.SystemObject))
            .Build();

        // Every endpoint without an explicit policy still requires authenticated system access.
        options.FallbackPolicy = access;
        options.AddPolicy(Authenticated, authenticated);
        options.AddPolicy(SystemAccess, access);
        options.AddPolicy(SystemManage, manage);
    }
}
