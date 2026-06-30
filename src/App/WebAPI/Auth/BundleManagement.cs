using System.Text.RegularExpressions;
using Shared.Auth;

namespace WebAPI.Auth;

public enum BundleOpStatus
{
    Ok,
    NotFound,
    Validation,
    Forbidden,
    Unavailable
}

public sealed record BundleOpResult(BundleOpStatus Status, string? Error = null)
{
    public static readonly BundleOpResult Ok = new(BundleOpStatus.Ok);

    public static BundleOpResult NotFound(string error) => new(BundleOpStatus.NotFound, error);

    public static BundleOpResult Validation(string error) => new(BundleOpStatus.Validation, error);

    public static BundleOpResult Forbidden(string error) => new(BundleOpStatus.Forbidden, error);

    public static BundleOpResult Unavailable(string error) => new(BundleOpStatus.Unavailable, error);
}

public sealed record BundleOpResult<T>(BundleOpStatus Status, T? Value = default, string? Error = null)
{
    public static BundleOpResult<T> Ok(T value) => new(BundleOpStatus.Ok, value);

    public static BundleOpResult<T> NotFound(string error) => new(BundleOpStatus.NotFound, Error: error);

    public static BundleOpResult<T> Unavailable(string error) => new(BundleOpStatus.Unavailable, Error: error);
}

public sealed record BundleGrant(string Type, string Id);

public sealed record BundleView(
    string Id,
    bool SystemOwned,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<BundleGrant> Grants);

public sealed record CatalogEntry(string Id, string Bundle);

/// <summary>
/// Runtime bundle-management surface (the hybrid feature in B_Axis1.MD). Lists the code-defined
/// endpoint catalog, exposes capability_group bundles + their grants, and lets privileged users
/// compose and grant their own <c>user.</c> bundles. System-owned (seeded) bundles are read-only:
/// their endpoint membership cannot be mutated here — OpenFGA does not enforce that, so this layer
/// must.
/// </summary>
public interface IBundleManagementService
{
    IReadOnlyList<CatalogEntry> GetCatalog();

    Task<BundleOpResult<IReadOnlyList<BundleView>>> ListBundlesAsync(CancellationToken cancellationToken);

    Task<BundleOpResult<BundleView>> GetBundleAsync(string bundleId, CancellationToken cancellationToken);

    Task<BundleOpResult> CreateBundleAsync(string bundleId, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken);

    Task<BundleOpResult> SetBundleEndpointsAsync(string bundleId, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken);

    Task<BundleOpResult> DeleteBundleAsync(string bundleId, CancellationToken cancellationToken);

    Task<BundleOpResult> GrantAsync(string bundleId, string granteeType, string granteeId, CancellationToken cancellationToken);

    Task<BundleOpResult> RevokeAsync(string bundleId, string granteeType, string granteeId, CancellationToken cancellationToken);
}

/// <summary>Single-user-mode stand-in: there is no OpenFGA store to manage.</summary>
public sealed class NullBundleManagementService : IBundleManagementService
{
    public IReadOnlyList<CatalogEntry> GetCatalog()
        => EndpointCatalog.Endpoints.Select(e => new CatalogEntry(e.Id, e.Bundle)).ToArray();

    private static BundleOpResult Disabled() =>
        BundleOpResult.Unavailable("Bundle management is unavailable in single-user mode.");

    public Task<BundleOpResult<IReadOnlyList<BundleView>>> ListBundlesAsync(CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult<IReadOnlyList<BundleView>>.Unavailable("Bundle management is unavailable in single-user mode."));

    public Task<BundleOpResult<BundleView>> GetBundleAsync(string bundleId, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult<BundleView>.Unavailable("Bundle management is unavailable in single-user mode."));

    public Task<BundleOpResult> CreateBundleAsync(string bundleId, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken)
        => Task.FromResult(Disabled());

    public Task<BundleOpResult> SetBundleEndpointsAsync(string bundleId, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken)
        => Task.FromResult(Disabled());

    public Task<BundleOpResult> DeleteBundleAsync(string bundleId, CancellationToken cancellationToken)
        => Task.FromResult(Disabled());

    public Task<BundleOpResult> GrantAsync(string bundleId, string granteeType, string granteeId, CancellationToken cancellationToken)
        => Task.FromResult(Disabled());

    public Task<BundleOpResult> RevokeAsync(string bundleId, string granteeType, string granteeId, CancellationToken cancellationToken)
        => Task.FromResult(Disabled());
}

public static partial class BundleManagementValidation
{
    // OpenFGA object-id-safe: no whitespace and no `:`/`#` separators. `.` is allowed so `user.` ids work.
    [GeneratedRegex(@"^[A-Za-z0-9_.@/+=,|-]+$")]
    public static partial Regex ValidIdRegex();

    public const string GranteeTypeUser = "user";
    public const string GranteeTypeGroup = "group";

    /// <summary>Builds the OpenFGA <c>user</c> field for a grant tuple, or null if the grantee is invalid.</summary>
    public static string? GranteeUser(string granteeType, string granteeId)
    {
        if (string.IsNullOrWhiteSpace(granteeId) || !ValidIdRegex().IsMatch(granteeId))
        {
            return null;
        }

        return granteeType switch
        {
            GranteeTypeUser => $"user:{granteeId}",
            GranteeTypeGroup => $"group:{granteeId}#member",
            _ => null
        };
    }

    /// <summary>Parses an OpenFGA grant <c>user</c> field back into a (type, id) grant, or null if unrecognized.</summary>
    public static BundleGrant? ParseGranteeUser(string user)
    {
        if (user.StartsWith("user:", StringComparison.Ordinal))
        {
            return new BundleGrant(GranteeTypeUser, user["user:".Length..]);
        }

        if (user.StartsWith("group:", StringComparison.Ordinal) && user.EndsWith("#member", StringComparison.Ordinal))
        {
            var inner = user["group:".Length..^"#member".Length];
            return new BundleGrant(GranteeTypeGroup, inner);
        }

        return null;
    }
}
