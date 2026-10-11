using System.Text.RegularExpressions;
using Shared.Auth;

namespace WebAPI.Auth;

public sealed class NullBundleManagementService : IBundleManagementService
{
    public IReadOnlyList<CatalogEntry> GetCatalog()
        => EndpointCatalog.Endpoints.Select(e => new CatalogEntry(e.Id, e.Bundle)).ToArray();

    private static BundleOpResult Disabled() =>
        BundleOpResult.Unavailable("Bundle management is unavailable in single-user mode.");

    public Task<BundleOpResult<IReadOnlyList<BundleView>>> ListBundlesAsync(CancellationToken cancellationToken)
    {
        var grouped = EndpointCatalog.Endpoints
            .GroupBy(x => x.Bundle, StringComparer.Ordinal)
            .Select(group => new BundleView(
                group.Key,
                SystemOwned: true,
                group.Select(x => x.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Grants: []));
        var all = new BundleView(
            AuthConstants.AllBundle,
            SystemOwned: true,
            EndpointCatalog.Endpoints.Select(x => x.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Grants: []);
        return Task.FromResult(BundleOpResult<IReadOnlyList<BundleView>>.Ok(
            grouped.Append(all).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray()));
    }

    public async Task<BundleOpResult<BundleView>> GetBundleAsync(string bundleId, CancellationToken cancellationToken)
    {
        var bundles = await ListBundlesAsync(cancellationToken);
        var bundle = bundles.Value!.FirstOrDefault(x => x.Id == bundleId);
        return bundle is null
            ? BundleOpResult<BundleView>.NotFound($"Bundle '{bundleId}' was not found.")
            : BundleOpResult<BundleView>.Ok(bundle);
    }

    public Task<BundleOpResult> CreateBundleAsync(string bundleId, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken)
        => Task.FromResult(Disabled());

    public Task<BundleOpResult> CreateBundleAsync(
        string bundleId,
        IReadOnlyCollection<string> endpointIds,
        string? cloneFrom,
        CancellationToken cancellationToken)
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
