using System.Security.Claims;

namespace Shared.Auth;

public static class AuthConstants
{
    public const string SingleUserScheme = "SingleUser";
    public const string SingleUserSubject = "single-user-owner";
    public static readonly Guid SingleUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Group membership: group:<name>#member@user:<sub>. The session-sync tuple writer keeps these
    // in step with the caller's Authentik groups.
    public const string MemberRelation = "member";

    // API-surface authorization (Axis 1). Every route is an `endpoint` object; endpoints are bundled
    // into `capability_group` objects; a user-group (or user) is granted a bundle. The per-request
    // check is `invoke` on `endpoint:<id>`.
    public const string InvokeRelation = "invoke";
    public const string BundleRelation = "bundle";
    public const string GranteeRelation = "grantee";

    public const string EndpointObjectPrefix = "endpoint:";
    public const string CapabilityGroupObjectPrefix = "capability_group:";

    /// <summary>Lock-out guard bundle: contains every endpoint and is granted to the bootstrap admin group.</summary>
    public const string AllBundle = "all";

    /// <summary>Reserved id prefix that marks a capability_group as runtime-composed (full CRUD). Anything
    /// without this prefix is a system-owned seeded bundle and is read-only to the management API.</summary>
    public const string UserBundlePrefix = "user.";

    public static string EndpointObject(string endpointId) => EndpointObjectPrefix + endpointId;

    public static string CapabilityGroupObject(string bundleId) => CapabilityGroupObjectPrefix + bundleId;

    public static bool IsUserBundle(string bundleId) =>
        bundleId.StartsWith(UserBundlePrefix, StringComparison.Ordinal);

    public const string SubjectClaim = "sub";
    public const string GroupsClaim = "groups";
    public const string PreferredUsernameClaim = "preferred_username";

    public static string? FindSubject(ClaimsPrincipal? principal)
        => principal is null
            ? null
            : FirstNonBlank(
                principal.FindFirst(SubjectClaim)?.Value,
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                principal.Identity?.Name);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
