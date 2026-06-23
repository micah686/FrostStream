using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebAPI.Auth;

/// <summary>
/// Reconciles a user's Authentik group membership into OpenFGA <c>group:&lt;name&gt;#member</c> tuples.
/// Adding a user to a group writes a tuple; removing them from a group deletes the stale tuple so
/// revoked memberships stop granting access.
/// </summary>
public interface IOpenFgaTupleWriter
{
    Task SyncUserGroupsAsync(string subject, IReadOnlyCollection<string> groups, CancellationToken cancellationToken = default);
}

/// <summary>Single-user-mode no-op; there is no external authorization server to sync to.</summary>
public sealed class NullOpenFgaTupleWriter : IOpenFgaTupleWriter
{
    public Task SyncUserGroupsAsync(string subject, IReadOnlyCollection<string> groups, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class OpenFgaTupleWriter(
    HttpClient httpClient,
    IOptions<OpenFgaOptions> options,
    OpenFgaRuntimeState state,
    ILogger<OpenFgaTupleWriter> logger) : IOpenFgaTupleWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly OpenFgaOptions _options = options.Value;

    public async Task SyncUserGroupsAsync(string subject, IReadOnlyCollection<string> groups, CancellationToken cancellationToken = default)
    {
        var storeId = state.StoreId;
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(storeId))
        {
            logger.LogWarning("OpenFGA is not configured; skipping group tuple sync for {Subject}.", subject);
            return;
        }

        var user = $"user:{subject}";
        var desired = groups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => $"group:{group.Trim()}")
            .ToHashSet(StringComparer.Ordinal);

        IReadOnlySet<string> existing;
        try
        {
            existing = await ReadExistingGroupObjectsAsync(storeId, user, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading existing OpenFGA group tuples for {Subject}; writing desired tuples without reconciliation.", subject);
            existing = new HashSet<string>(StringComparer.Ordinal);
        }

        var toAdd = desired.Except(existing).ToArray();
        var toDelete = existing.Except(desired).ToArray();

        if (toAdd.Length > 0)
        {
            await WriteAsync(storeId, "writes", toAdd.Select(obj => Tuple(user, obj)), cancellationToken);
        }

        if (toDelete.Length > 0)
        {
            await WriteAsync(storeId, "deletes", toDelete.Select(obj => Tuple(user, obj)), cancellationToken);
        }

        logger.LogInformation(
            "Synced OpenFGA group tuples for {Subject}: +{Added} -{Removed}.",
            subject,
            toAdd.Length,
            toDelete.Length);
    }

    private async Task<IReadOnlySet<string>> ReadExistingGroupObjectsAsync(string storeId, string user, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/read");
        request.Content = JsonContent.Create(new
        {
            tuple_key = Tuple(user, "group:")
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var result = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("tuples", out var tuples) && tuples.ValueKind == JsonValueKind.Array)
        {
            foreach (var tuple in tuples.EnumerateArray())
            {
                if (tuple.TryGetProperty("key", out var key) &&
                    key.TryGetProperty("object", out var obj) &&
                    obj.GetString() is { Length: > 0 } objectValue)
                {
                    result.Add(objectValue);
                }
            }
        }

        return result;
    }

    private async Task WriteAsync(string storeId, string operation, IEnumerable<object> tupleKeys, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            [operation] = new { tuple_keys = tupleKeys.ToArray() }
        };
        if (!string.IsNullOrWhiteSpace(state.AuthorizationModelId))
        {
            payload["authorization_model_id"] = state.AuthorizationModelId;
        }

        using var request = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/write");
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Duplicate-write / missing-delete races are expected during idempotent re-syncs.
            logger.LogWarning("OpenFGA {Operation} returned {StatusCode}: {Body}", operation, (int)response.StatusCode, body);
        }
    }

    private static object Tuple(string user, string @object) => new { user, relation = "member", @object };

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
