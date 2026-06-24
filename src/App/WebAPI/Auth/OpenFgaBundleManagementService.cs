using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared.Auth;

namespace WebAPI.Auth;

/// <summary>
/// OpenFGA-backed implementation of <see cref="IBundleManagementService"/>. Bundles are pure tuple
/// state: a capability_group exists exactly as long as some tuple references it. Seeded baseline
/// bundles are identified by the absence of the <c>user.</c> id prefix and are read-only here.
/// </summary>
public sealed class OpenFgaBundleManagementService(
    HttpClient httpClient,
    IOptions<OpenFgaOptions> options,
    OpenFgaRuntimeState state,
    ILogger<OpenFgaBundleManagementService> logger) : IBundleManagementService
{
    public const string HttpClientName = "openfga-bundle-management";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly OpenFgaOptions _options = options.Value;

    private sealed record Tuple(string User, string Relation, string Object);

    public IReadOnlyList<CatalogEntry> GetCatalog()
        => EndpointCatalog.Endpoints.Select(e => new CatalogEntry(e.Id, e.Bundle)).ToArray();

    public async Task<BundleOpResult<IReadOnlyList<BundleView>>> ListBundlesAsync(CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
        {
            return BundleOpResult<IReadOnlyList<BundleView>>.Unavailable("OpenFGA is not configured.");
        }

        List<Tuple> all;
        try
        {
            all = await ReadAllTuplesAsync(storeId, tupleKey: null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading tuples while listing bundles.");
            return BundleOpResult<IReadOnlyList<BundleView>>.Unavailable("OpenFGA read failed.");
        }

        var endpoints = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var grants = new Dictionary<string, List<BundleGrant>>(StringComparer.Ordinal);

        foreach (var tuple in all)
        {
            if (tuple.Relation == AuthConstants.BundleRelation &&
                tuple.User.StartsWith(AuthConstants.CapabilityGroupObjectPrefix, StringComparison.Ordinal) &&
                tuple.Object.StartsWith(AuthConstants.EndpointObjectPrefix, StringComparison.Ordinal))
            {
                var bundleId = tuple.User[AuthConstants.CapabilityGroupObjectPrefix.Length..];
                var endpointId = tuple.Object[AuthConstants.EndpointObjectPrefix.Length..];
                (endpoints.TryGetValue(bundleId, out var set) ? set : endpoints[bundleId] = new SortedSet<string>(StringComparer.Ordinal)).Add(endpointId);
            }
            else if (tuple.Relation == AuthConstants.GranteeRelation &&
                     tuple.Object.StartsWith(AuthConstants.CapabilityGroupObjectPrefix, StringComparison.Ordinal))
            {
                var bundleId = tuple.Object[AuthConstants.CapabilityGroupObjectPrefix.Length..];
                if (BundleManagementValidation.ParseGranteeUser(tuple.User) is { } grant)
                {
                    (grants.TryGetValue(bundleId, out var list) ? list : grants[bundleId] = []).Add(grant);
                }
            }
        }

        var bundleIds = endpoints.Keys.Concat(grants.Keys).ToHashSet(StringComparer.Ordinal);
        var views = bundleIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new BundleView(
                id,
                SystemOwned: !AuthConstants.IsUserBundle(id),
                Endpoints: endpoints.TryGetValue(id, out var set) ? set.ToArray() : [],
                Grants: grants.TryGetValue(id, out var list)
                    ? list.OrderBy(g => g.Type, StringComparer.Ordinal).ThenBy(g => g.Id, StringComparer.Ordinal).ToArray()
                    : []))
            .ToArray();

        return BundleOpResult<IReadOnlyList<BundleView>>.Ok(views);
    }

    public async Task<BundleOpResult<BundleView>> GetBundleAsync(string bundleId, CancellationToken cancellationToken)
    {
        var list = await ListBundlesAsync(cancellationToken);
        if (list.Status != BundleOpStatus.Ok)
        {
            return new BundleOpResult<BundleView>(list.Status, Error: list.Error);
        }

        var match = list.Value!.FirstOrDefault(b => b.Id == bundleId);
        return match is null
            ? BundleOpResult<BundleView>.NotFound($"Bundle '{bundleId}' was not found.")
            : BundleOpResult<BundleView>.Ok(match);
    }

    public async Task<BundleOpResult> CreateBundleAsync(string bundleId, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
        {
            return BundleOpResult.Unavailable("OpenFGA is not configured.");
        }

        if (ValidateUserBundleId(bundleId) is { } idError)
        {
            return idError;
        }

        if (endpointIds.Count == 0)
        {
            return BundleOpResult.Validation("A bundle must contain at least one endpoint.");
        }

        if (ValidateEndpoints(endpointIds) is { } endpointError)
        {
            return endpointError;
        }

        var existing = await ReadBundleEndpointObjectsAsync(storeId, bundleId, cancellationToken);
        if (existing.Count > 0)
        {
            return BundleOpResult.Validation($"Bundle '{bundleId}' already exists; use set-endpoints to modify it.");
        }

        var cgObject = AuthConstants.CapabilityGroupObject(bundleId);
        var writes = endpointIds.Distinct(StringComparer.Ordinal)
            .Select(id => new Tuple(cgObject, AuthConstants.BundleRelation, AuthConstants.EndpointObject(id)))
            .ToArray();

        return await WriteAsync(storeId, writes, deletes: [], cancellationToken);
    }

    public async Task<BundleOpResult> SetBundleEndpointsAsync(string bundleId, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
        {
            return BundleOpResult.Unavailable("OpenFGA is not configured.");
        }

        if (ValidateUserBundleId(bundleId) is { } idError)
        {
            return idError;
        }

        if (ValidateEndpoints(endpointIds) is { } endpointError)
        {
            return endpointError;
        }

        var cgObject = AuthConstants.CapabilityGroupObject(bundleId);
        var existing = await ReadBundleEndpointObjectsAsync(storeId, bundleId, cancellationToken);
        if (existing.Count == 0)
        {
            return BundleOpResult.NotFound($"Bundle '{bundleId}' was not found.");
        }

        var desired = endpointIds.Distinct(StringComparer.Ordinal)
            .Select(AuthConstants.EndpointObject)
            .ToHashSet(StringComparer.Ordinal);

        var writes = desired.Except(existing)
            .Select(obj => new Tuple(cgObject, AuthConstants.BundleRelation, obj)).ToArray();
        var deletes = existing.Except(desired)
            .Select(obj => new Tuple(cgObject, AuthConstants.BundleRelation, obj)).ToArray();

        if (writes.Length == 0 && deletes.Length == 0)
        {
            return BundleOpResult.Ok;
        }

        return await WriteAsync(storeId, writes, deletes, cancellationToken);
    }

    public async Task<BundleOpResult> DeleteBundleAsync(string bundleId, CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
        {
            return BundleOpResult.Unavailable("OpenFGA is not configured.");
        }

        if (ValidateUserBundleId(bundleId) is { } idError)
        {
            return idError;
        }

        var cgObject = AuthConstants.CapabilityGroupObject(bundleId);
        var endpointObjects = await ReadBundleEndpointObjectsAsync(storeId, bundleId, cancellationToken);
        var grantTuples = await ReadBundleGrantTuplesAsync(storeId, bundleId, cancellationToken);

        if (endpointObjects.Count == 0 && grantTuples.Count == 0)
        {
            return BundleOpResult.NotFound($"Bundle '{bundleId}' was not found.");
        }

        var deletes = endpointObjects
            .Select(obj => new Tuple(cgObject, AuthConstants.BundleRelation, obj))
            .Concat(grantTuples)
            .ToArray();

        return await WriteAsync(storeId, writes: [], deletes, cancellationToken);
    }

    public async Task<BundleOpResult> GrantAsync(string bundleId, string granteeType, string granteeId, CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
        {
            return BundleOpResult.Unavailable("OpenFGA is not configured.");
        }

        if (BundleManagementValidation.GranteeUser(granteeType, granteeId) is not { } granteeUser)
        {
            return BundleOpResult.Validation("Grantee must be a valid user or group id.");
        }

        if (!await BundleExistsAsync(storeId, bundleId, cancellationToken))
        {
            return BundleOpResult.NotFound($"Bundle '{bundleId}' was not found.");
        }

        var tuple = new Tuple(granteeUser, AuthConstants.GranteeRelation, AuthConstants.CapabilityGroupObject(bundleId));
        return await WriteAsync(storeId, [tuple], deletes: [], cancellationToken);
    }

    public async Task<BundleOpResult> RevokeAsync(string bundleId, string granteeType, string granteeId, CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
        {
            return BundleOpResult.Unavailable("OpenFGA is not configured.");
        }

        if (BundleManagementValidation.GranteeUser(granteeType, granteeId) is not { } granteeUser)
        {
            return BundleOpResult.Validation("Grantee must be a valid user or group id.");
        }

        var grantTuples = await ReadBundleGrantTuplesAsync(storeId, bundleId, cancellationToken);
        var match = grantTuples.FirstOrDefault(t => t.User == granteeUser);
        if (match is null)
        {
            return BundleOpResult.NotFound("Grant was not found.");
        }

        return await WriteAsync(storeId, writes: [], [match], cancellationToken);
    }

    // ---- validation helpers ----

    private static BundleOpResult? ValidateUserBundleId(string bundleId)
    {
        if (!AuthConstants.IsUserBundle(bundleId))
        {
            return BundleOpResult.Forbidden(
                $"Bundle '{bundleId}' is system-owned and read-only. Runtime bundles must use the '{AuthConstants.UserBundlePrefix}' id prefix.");
        }

        if (!BundleManagementValidation.ValidIdRegex().IsMatch(bundleId))
        {
            return BundleOpResult.Validation("Bundle id contains characters that are not valid in an OpenFGA object id.");
        }

        return null;
    }

    private static BundleOpResult? ValidateEndpoints(IReadOnlyCollection<string> endpointIds)
    {
        var unknown = endpointIds.Where(id => !EndpointCatalog.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        return unknown.Length > 0
            ? BundleOpResult.Validation($"Unknown endpoint id(s): {string.Join(", ", unknown)}. Bundles may only reference the code-defined catalog.")
            : null;
    }

    private async Task<bool> BundleExistsAsync(string storeId, string bundleId, CancellationToken cancellationToken)
    {
        if (bundleId == AuthConstants.AllBundle || EndpointCatalog.SeededBundleIds.Contains(bundleId))
        {
            return true;
        }

        var endpoints = await ReadBundleEndpointObjectsAsync(storeId, bundleId, cancellationToken);
        return endpoints.Count > 0;
    }

    // ---- OpenFGA HTTP ----

    private string? StoreId
    {
        get
        {
            var storeId = state.StoreId;
            return string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(storeId) ? null : storeId;
        }
    }

    private async Task<HashSet<string>> ReadBundleEndpointObjectsAsync(string storeId, string bundleId, CancellationToken cancellationToken)
    {
        var tupleKey = new
        {
            user = AuthConstants.CapabilityGroupObject(bundleId),
            relation = AuthConstants.BundleRelation,
            @object = AuthConstants.EndpointObjectPrefix
        };

        var tuples = await ReadAllTuplesAsync(storeId, tupleKey, cancellationToken);
        return tuples.Select(t => t.Object).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<Tuple>> ReadBundleGrantTuplesAsync(string storeId, string bundleId, CancellationToken cancellationToken)
    {
        var tupleKey = new
        {
            relation = AuthConstants.GranteeRelation,
            @object = AuthConstants.CapabilityGroupObject(bundleId)
        };

        return await ReadAllTuplesAsync(storeId, tupleKey, cancellationToken);
    }

    private async Task<List<Tuple>> ReadAllTuplesAsync(string storeId, object? tupleKey, CancellationToken cancellationToken)
    {
        var results = new List<Tuple>();
        string? continuationToken = null;

        do
        {
            using var request = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/read");
            var payload = new Dictionary<string, object?> { ["page_size"] = 100 };
            if (tupleKey is not null)
            {
                payload["tuple_key"] = tupleKey;
            }

            if (!string.IsNullOrEmpty(continuationToken))
            {
                payload["continuation_token"] = continuationToken;
            }

            request.Content = JsonContent.Create(payload, options: JsonOptions);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("tuples", out var tuples) && tuples.ValueKind == JsonValueKind.Array)
            {
                foreach (var tuple in tuples.EnumerateArray())
                {
                    if (tuple.TryGetProperty("key", out var key) &&
                        key.TryGetProperty("user", out var user) &&
                        key.TryGetProperty("relation", out var relation) &&
                        key.TryGetProperty("object", out var obj))
                    {
                        results.Add(new Tuple(user.GetString() ?? "", relation.GetString() ?? "", obj.GetString() ?? ""));
                    }
                }
            }

            continuationToken = doc.RootElement.TryGetProperty("continuation_token", out var token)
                ? token.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        return results;
    }

    private async Task<BundleOpResult> WriteAsync(string storeId, IReadOnlyCollection<Tuple> writes, IReadOnlyCollection<Tuple> deletes, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(state.AuthorizationModelId))
        {
            payload["authorization_model_id"] = state.AuthorizationModelId;
        }

        if (writes.Count > 0)
        {
            payload["writes"] = new { tuple_keys = writes.Select(ToTupleKey).ToArray() };
        }

        if (deletes.Count > 0)
        {
            payload["deletes"] = new { tuple_keys = deletes.Select(ToTupleKey).ToArray() };
        }

        using var request = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/write");
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return BundleOpResult.Ok;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning("OpenFGA bundle write failed ({StatusCode}): {Body}", (int)response.StatusCode, body);
        return BundleOpResult.Unavailable("OpenFGA write failed.");
    }

    private static object ToTupleKey(Tuple tuple) => new { user = tuple.User, relation = tuple.Relation, @object = tuple.Object };

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_options.Endpoint.TrimEnd('/')}{path}");
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }

        return request;
    }
}
