using Microsoft.AspNetCore.Mvc;
using WebAPI.Auth;

namespace WebAPI.Features.Management.Controllers;

/// <summary>
/// Runtime authorization-bundle management (Axis 1 hybrid feature). Lists the code-defined endpoint
/// catalog, exposes capability_group bundles + grants, and lets privileged users compose and grant
/// their own <c>user.</c> bundles from the catalog. Seeded baseline bundles are read-only here. Every
/// action lives in the <c>management</c> baseline bundle and therefore in the <c>:all</c> bootstrap
/// bundle, so the bootstrap admin can always reach it.
/// </summary>
[ApiController]
[Route("api/global/management")]
public sealed class BundleManagementController(IBundleManagementService bundles, IDirectoryService directory) : ControllerBase
{
    [HttpGet("directory")]
    [Endpoint(EndpointIds.ManagementDirectorySearch)]
    [EndpointSummary("Search the identity provider directory")]
    [EndpointDescription("Searches Authentik for users or groups matching the query, for grantee autocomplete. User results carry the OIDC subject id (user UUID); group results carry the group name. Returns an empty list when the directory is unavailable or unconfigured.")]
    public async Task<ActionResult<IReadOnlyList<DirectoryEntry>>> SearchDirectory(
        [FromQuery] string type,
        [FromQuery] string q,
        CancellationToken cancellationToken)
        => Ok(await directory.SearchAsync(type ?? "", q ?? "", cancellationToken));

    [HttpGet("catalog")]
    [Endpoint(EndpointIds.ManagementCatalog)]
    [EndpointSummary("List the endpoint catalog")]
    [EndpointDescription("Returns every code-defined endpoint id and its seeded baseline bundle. Runtime bundles may only reference ids from this catalog.")]
    public ActionResult<IReadOnlyList<CatalogEntry>> GetCatalog() => Ok(bundles.GetCatalog());

    [HttpGet("bundles")]
    [Endpoint(EndpointIds.ManagementBundlesList)]
    [EndpointSummary("List capability bundles")]
    [EndpointDescription("Returns every capability bundle with its endpoint membership and grants. System-owned (seeded) bundles are flagged and are read-only.")]
    public async Task<ActionResult<IReadOnlyList<BundleView>>> ListBundles(CancellationToken cancellationToken)
    {
        var result = await bundles.ListBundlesAsync(cancellationToken);
        if (result.Status == BundleOpStatus.Ok)
        {
            result = BundleOpResult<IReadOnlyList<BundleView>>.Ok(await WithUserDisplayNamesAsync(result.Value!, cancellationToken));
        }

        return Map(result);
    }

    [HttpGet("bundles/{bundleId}")]
    [Endpoint(EndpointIds.ManagementBundlesGet)]
    [EndpointSummary("Get a capability bundle")]
    [EndpointDescription("Returns a single capability bundle with its endpoint membership and grants. Returns 404 when the bundle has no tuples.")]
    public async Task<ActionResult<BundleView>> GetBundle(string bundleId, CancellationToken cancellationToken)
    {
        var result = await bundles.GetBundleAsync(bundleId, cancellationToken);
        if (result.Status == BundleOpStatus.Ok)
        {
            var enriched = await WithUserDisplayNamesAsync([result.Value!], cancellationToken);
            result = BundleOpResult<BundleView>.Ok(enriched[0]);
        }

        return Map(result);
    }

    [HttpPost("bundles")]
    [Endpoint(EndpointIds.ManagementBundlesCreate)]
    [EndpointSummary("Create a runtime bundle")]
    [EndpointDescription("Creates a user-composed bundle from catalog endpoints. The id must use the 'user.' prefix; system-owned ids are rejected.")]
    public async Task<IActionResult> CreateBundle([FromBody] CreateBundleRequest request, CancellationToken cancellationToken)
    {
        var result = await bundles.CreateBundleAsync(request.Id ?? "", request.Endpoints ?? [], cancellationToken);
        return result.Status == BundleOpStatus.Ok
            ? CreatedAtAction(nameof(GetBundle), new { bundleId = request.Id }, null)
            : MapError(result);
    }

    [HttpPut("bundles/{bundleId}/endpoints")]
    [Endpoint(EndpointIds.ManagementBundlesSetEndpoints)]
    [EndpointSummary("Set a runtime bundle's endpoints")]
    [EndpointDescription("Replaces the endpoint membership of an existing user bundle with the supplied catalog ids. Seeded bundles cannot be modified.")]
    public async Task<IActionResult> SetEndpoints(string bundleId, [FromBody] SetEndpointsRequest request, CancellationToken cancellationToken)
        => Map(await bundles.SetBundleEndpointsAsync(bundleId, request.Endpoints ?? [], cancellationToken));

    [HttpDelete("bundles/{bundleId}")]
    [Endpoint(EndpointIds.ManagementBundlesDelete)]
    [EndpointSummary("Delete a runtime bundle")]
    [EndpointDescription("Deletes a user bundle and all of its endpoint memberships and grants. Seeded bundles cannot be deleted.")]
    public async Task<IActionResult> DeleteBundle(string bundleId, CancellationToken cancellationToken)
        => Map(await bundles.DeleteBundleAsync(bundleId, cancellationToken));

    [HttpPost("bundles/{bundleId}/grants")]
    [Endpoint(EndpointIds.ManagementGrantsCreate)]
    [EndpointSummary("Grant a bundle to a user or group")]
    [EndpointDescription("Grants a capability bundle to a user or group, letting them invoke every endpoint in the bundle. Grants may target seeded or user bundles.")]
    public async Task<IActionResult> Grant(string bundleId, [FromBody] GrantRequest request, CancellationToken cancellationToken)
        => Map(await bundles.GrantAsync(bundleId, request.Type ?? "", request.Id ?? "", cancellationToken));

    [HttpDelete("bundles/{bundleId}/grants")]
    [Endpoint(EndpointIds.ManagementGrantsDelete)]
    [EndpointSummary("Revoke a bundle grant")]
    [EndpointDescription("Revokes a previously granted bundle from a user or group. The grantee type and id are supplied as query parameters.")]
    public async Task<IActionResult> Revoke(
        string bundleId,
        [FromQuery] string type,
        [FromQuery] string id,
        CancellationToken cancellationToken)
        => Map(await bundles.RevokeAsync(bundleId, type ?? "", id ?? "", cancellationToken));

    /// <summary>Resolves user-grant subject UUIDs to friendly names so the UI can show who a grant
    /// targets. Group grants already carry their name as the id. Unresolvable ids stay name-less.</summary>
    private async Task<IReadOnlyList<BundleView>> WithUserDisplayNamesAsync(
        IReadOnlyList<BundleView> views,
        CancellationToken cancellationToken)
    {
        var userIds = views
            .SelectMany(view => view.Grants)
            .Where(grant => grant.Type == BundleManagementValidation.GranteeTypeUser)
            .Select(grant => grant.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (userIds.Length == 0)
        {
            return views;
        }

        var names = await directory.ResolveUserNamesAsync(userIds, cancellationToken);
        if (names.Count == 0)
        {
            return views;
        }

        return views
            .Select(view => view with
            {
                Grants = view.Grants
                    .Select(grant => grant.Type == BundleManagementValidation.GranteeTypeUser && names.TryGetValue(grant.Id, out var name)
                        ? grant with { DisplayName = name }
                        : grant)
                    .ToArray()
            })
            .ToArray();
    }

    private ActionResult<T> Map<T>(BundleOpResult<T> result)
        => result.Status == BundleOpStatus.Ok ? Ok(result.Value) : MapError(new BundleOpResult(result.Status, result.Error));

    private IActionResult Map(BundleOpResult result)
        => result.Status == BundleOpStatus.Ok ? NoContent() : MapError(result);

    private ActionResult MapError(BundleOpResult result) => result.Status switch
    {
        BundleOpStatus.NotFound => NotFound(result.Error),
        BundleOpStatus.Validation => BadRequest(result.Error),
        BundleOpStatus.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result.Error),
        BundleOpStatus.Unavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, result.Error),
        _ => StatusCode(StatusCodes.Status500InternalServerError, result.Error ?? "Bundle management failed.")
    };
}

public sealed record CreateBundleRequest
{
    public string? Id { get; init; }

    public IReadOnlyList<string>? Endpoints { get; init; }
}

public sealed record SetEndpointsRequest
{
    public IReadOnlyList<string>? Endpoints { get; init; }
}

public sealed record GrantRequest
{
    public string? Type { get; init; }

    public string? Id { get; init; }
}
