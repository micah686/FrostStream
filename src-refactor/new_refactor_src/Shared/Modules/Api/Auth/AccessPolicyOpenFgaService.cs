using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Shared.Messaging;

namespace WebAPI.Auth;

public interface IAccessPolicyOpenFgaService
{
    Task<BundleOpResult> SynchronizeAsync(AccessPolicyDto policy, CancellationToken cancellationToken);
    Task<BundleOpResult> RemoveAsync(Guid policyId, CancellationToken cancellationToken);
    Task<BundleOpResult<IReadOnlyList<string>>> ListEffectiveEndpointsAsync(
        string principalType, string principalId, CancellationToken cancellationToken);
    Task<BundleOpResult<IReadOnlyList<string>>> ListUserGroupsAsync(
        string userSubject, CancellationToken cancellationToken);
}

public sealed class NullAccessPolicyOpenFgaService : IAccessPolicyOpenFgaService
{
    public Task<BundleOpResult> SynchronizeAsync(AccessPolicyDto policy, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult.Ok);

    public Task<BundleOpResult> RemoveAsync(Guid policyId, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult.Ok);

    public Task<BundleOpResult<IReadOnlyList<string>>> ListEffectiveEndpointsAsync(
        string principalType, string principalId, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult<IReadOnlyList<string>>.Ok(
            EndpointCatalog.Endpoints.Select(x => x.Id).ToArray()));

    public Task<BundleOpResult<IReadOnlyList<string>>> ListUserGroupsAsync(
        string userSubject, CancellationToken cancellationToken)
        => Task.FromResult(BundleOpResult<IReadOnlyList<string>>.Ok(["admins"]));
}

public sealed class OpenFgaAccessPolicyService(
    HttpClient httpClient,
    IOptions<OpenFgaOptions> options,
    OpenFgaRuntimeState state,
    ILogger<OpenFgaAccessPolicyService> logger) : IAccessPolicyOpenFgaService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly OpenFgaOptions _options = options.Value;
    private sealed record Tuple(string User, string Relation, string Object);

    public async Task<BundleOpResult> SynchronizeAsync(AccessPolicyDto policy, CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
            return BundleOpResult.Unavailable("OpenFGA is not configured.");

        try
        {
            var policyObject = AuthConstants.AccessPolicyObject(policy.PolicyId);
            var existingGrantees = await ReadTuplesAsync(
                storeId,
                new
                {
                    relation = AuthConstants.GranteeRelation,
                    @object = policyObject
                },
                cancellationToken);
            var existingBundles = await ReadTuplesAsync(
                storeId,
                new
                {
                    user = policyObject,
                    relation = AuthConstants.PolicyRelation,
                    @object = AuthConstants.CapabilityGroupObjectPrefix
                },
                cancellationToken);
            var existing = existingGrantees
                .Concat(existingBundles)
                .ToHashSet();

            var desired = new HashSet<Tuple>();
            if (policy.Enabled)
            {
                foreach (var bundleId in policy.BundleIds)
                {
                    desired.Add(new Tuple(
                        policyObject,
                        AuthConstants.PolicyRelation,
                        AuthConstants.CapabilityGroupObject(bundleId)));
                }

                foreach (var assignment in policy.Assignments)
                {
                    if (BundleManagementValidation.GranteeUser(assignment.Type, assignment.Id) is { } grantee)
                    {
                        desired.Add(new Tuple(grantee, AuthConstants.GranteeRelation, policyObject));
                    }
                }
            }

            return await WriteAsync(
                storeId,
                desired.Except(existing).ToArray(),
                existing.Except(desired).ToArray(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed synchronizing access policy {PolicyId}.", policy.PolicyId);
            return BundleOpResult.Unavailable("OpenFGA policy synchronization failed.");
        }
    }

    public async Task<BundleOpResult> RemoveAsync(Guid policyId, CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
            return BundleOpResult.Unavailable("OpenFGA is not configured.");

        try
        {
            var policyObject = AuthConstants.AccessPolicyObject(policyId);
            var existingGrantees = await ReadTuplesAsync(
                storeId,
                new
                {
                    relation = AuthConstants.GranteeRelation,
                    @object = policyObject
                },
                cancellationToken);
            var existingBundles = await ReadTuplesAsync(
                storeId,
                new
                {
                    user = policyObject,
                    relation = AuthConstants.PolicyRelation,
                    @object = AuthConstants.CapabilityGroupObjectPrefix
                },
                cancellationToken);
            var existing = existingGrantees
                .Concat(existingBundles)
                .ToArray();
            return await WriteAsync(storeId, [], existing, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed removing access policy {PolicyId}.", policyId);
            return BundleOpResult.Unavailable("OpenFGA policy removal failed.");
        }
    }

    public async Task<BundleOpResult<IReadOnlyList<string>>> ListEffectiveEndpointsAsync(
        string principalType,
        string principalId,
        CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
            return BundleOpResult<IReadOnlyList<string>>.Unavailable("OpenFGA is not configured.");

        var user = BundleManagementValidation.GranteeUser(principalType, principalId);
        if (user is null)
            return new BundleOpResult<IReadOnlyList<string>>(BundleOpStatus.Validation, Error: "Invalid principal.");

        try
        {
            using var request = NewRequest(HttpMethod.Post,
                $"/stores/{Uri.EscapeDataString(storeId)}/list-objects");
            request.Content = JsonContent.Create(new
            {
                authorization_model_id = NullIfBlank(state.AuthorizationModelId),
                type = "endpoint",
                relation = AuthConstants.InvokeRelation,
                user
            }, options: JsonOptions);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var values = doc.RootElement.TryGetProperty("objects", out var objects)
                ? objects.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => x.StartsWith(AuthConstants.EndpointObjectPrefix, StringComparison.Ordinal))
                    .Select(x => x[AuthConstants.EndpointObjectPrefix.Length..])
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];
            return BundleOpResult<IReadOnlyList<string>>.Ok(values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed listing effective endpoints for {PrincipalType}:{PrincipalId}.", principalType, principalId);
            return BundleOpResult<IReadOnlyList<string>>.Unavailable("OpenFGA effective-access query failed.");
        }
    }

    public async Task<BundleOpResult<IReadOnlyList<string>>> ListUserGroupsAsync(
        string userSubject,
        CancellationToken cancellationToken)
    {
        if (StoreId is not { } storeId)
            return BundleOpResult<IReadOnlyList<string>>.Unavailable("OpenFGA is not configured.");

        try
        {
            var user = $"user:{userSubject}";
            var groups = (await ReadTuplesAsync(
                    storeId,
                    new
                    {
                        user,
                        relation = AuthConstants.MemberRelation,
                        @object = "group:"
                    },
                    cancellationToken))
                .Where(x => x.User == user &&
                            x.Relation == AuthConstants.MemberRelation &&
                            x.Object.StartsWith("group:", StringComparison.Ordinal))
                .Select(x => x.Object["group:".Length..])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return BundleOpResult<IReadOnlyList<string>>.Ok(groups);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed listing OpenFGA group membership for a user.");
            return BundleOpResult<IReadOnlyList<string>>.Unavailable("OpenFGA membership query failed.");
        }
    }

    private string? StoreId =>
        string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(state.StoreId)
            ? null
            : state.StoreId;

    private async Task<List<Tuple>> ReadTuplesAsync(
        string storeId,
        object tupleKey,
        CancellationToken cancellationToken)
    {
        var results = new List<Tuple>();
        string? continuationToken = null;
        do
        {
            using var request = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/read");
            var payload = new Dictionary<string, object?> { ["page_size"] = 100 };
            payload["tuple_key"] = tupleKey;
            if (!string.IsNullOrEmpty(continuationToken))
                payload["continuation_token"] = continuationToken;
            request.Content = JsonContent.Create(payload, options: JsonOptions);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("tuples", out var tuples))
            {
                foreach (var value in tuples.EnumerateArray())
                {
                    var key = value.GetProperty("key");
                    results.Add(new Tuple(
                        key.GetProperty("user").GetString() ?? "",
                        key.GetProperty("relation").GetString() ?? "",
                        key.GetProperty("object").GetString() ?? ""));
                }
            }
            continuationToken = doc.RootElement.TryGetProperty("continuation_token", out var token)
                ? token.GetString()
                : null;
        } while (!string.IsNullOrEmpty(continuationToken));
        return results;
    }

    private async Task<BundleOpResult> WriteAsync(
        string storeId,
        IReadOnlyCollection<Tuple> writes,
        IReadOnlyCollection<Tuple> deletes,
        CancellationToken cancellationToken)
    {
        if (writes.Count == 0 && deletes.Count == 0)
            return BundleOpResult.Ok;

        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(state.AuthorizationModelId))
            payload["authorization_model_id"] = state.AuthorizationModelId;
        if (writes.Count > 0)
            payload["writes"] = new { tuple_keys = writes.Select(ToTupleKey).ToArray() };
        if (deletes.Count > 0)
            payload["deletes"] = new { tuple_keys = deletes.Select(ToTupleKey).ToArray() };

        using var request = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/write");
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? BundleOpResult.Ok
            : BundleOpResult.Unavailable("OpenFGA policy write failed.");
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_options.Endpoint.TrimEnd('/')}{path}");
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        return request;
    }

    private static object ToTupleKey(Tuple tuple)
        => new { user = tuple.User, relation = tuple.Relation, @object = tuple.Object };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
