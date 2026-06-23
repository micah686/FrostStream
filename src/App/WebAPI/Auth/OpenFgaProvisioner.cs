using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared.Auth;

namespace WebAPI.Auth;

/// <summary>
/// On startup (multi-user mode) ensures the OpenFGA store, authorization model, and bootstrap
/// tuples exist, then publishes the resolved ids into <see cref="OpenFgaRuntimeState"/> so the
/// authorizer can make checks without manual <c>OPENFGA_STORE_ID</c>/<c>OPENFGA_AUTHORIZATION_MODEL_ID</c>
/// configuration. Explicitly-configured ids are honoured and never overwritten.
/// </summary>
public sealed class OpenFgaProvisioner(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenFgaOptions> options,
    OpenFgaRuntimeState state,
    ILogger<OpenFgaProvisioner> logger) : BackgroundService
{
    public const string HttpClientName = "openfga-provisioner";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly OpenFgaOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoProvision)
        {
            logger.LogInformation("OpenFGA auto-provisioning disabled; using configured store/model ids.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            logger.LogWarning("OpenFGA endpoint is not configured; authorization will deny until it is set.");
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            var storeId = state.StoreId ?? await EnsureStoreAsync(client, stoppingToken);
            state.StoreId = storeId;

            var modelId = state.AuthorizationModelId ?? await EnsureModelAsync(client, storeId, stoppingToken);
            state.AuthorizationModelId = modelId;

            await WriteBootstrapTuplesAsync(client, storeId, modelId, stoppingToken);

            logger.LogInformation("OpenFGA provisioned. Store {StoreId}, model {ModelId}.", storeId, modelId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenFGA provisioning failed; multi-user authorization will deny until resolved.");
        }
    }

    private async Task<string> EnsureStoreAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using (var listRequest = NewRequest(HttpMethod.Get, "/stores?page_size=100"))
        using (var listResponse = await client.SendAsync(listRequest, cancellationToken))
        {
            listResponse.EnsureSuccessStatusCode();
            await using var stream = await listResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("stores", out var stores) && stores.ValueKind == JsonValueKind.Array)
            {
                foreach (var store in stores.EnumerateArray())
                {
                    if (store.TryGetProperty("name", out var name) &&
                        string.Equals(name.GetString(), _options.StoreName, StringComparison.Ordinal) &&
                        store.TryGetProperty("id", out var id) &&
                        id.GetString() is { Length: > 0 } storeId)
                    {
                        logger.LogInformation("Found existing OpenFGA store '{StoreName}' ({StoreId}).", _options.StoreName, storeId);
                        return storeId;
                    }
                }
            }
        }

        using var createRequest = NewRequest(HttpMethod.Post, "/stores");
        createRequest.Content = JsonContent.Create(new { name = _options.StoreName });
        using var createResponse = await client.SendAsync(createRequest, cancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var createdId = await ReadStringPropertyAsync(createResponse, "id", cancellationToken)
            ?? throw new InvalidOperationException("OpenFGA store creation returned no id.");
        logger.LogInformation("Created OpenFGA store '{StoreName}' ({StoreId}).", _options.StoreName, createdId);
        return createdId;
    }

    private async Task<string> EnsureModelAsync(HttpClient client, string storeId, CancellationToken cancellationToken)
    {
        using (var listRequest = NewRequest(HttpMethod.Get, $"/stores/{Uri.EscapeDataString(storeId)}/authorization-models?page_size=1"))
        using (var listResponse = await client.SendAsync(listRequest, cancellationToken))
        {
            listResponse.EnsureSuccessStatusCode();
            await using var stream = await listResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("authorization_models", out var models) &&
                models.ValueKind == JsonValueKind.Array &&
                models.GetArrayLength() > 0 &&
                models[0].TryGetProperty("id", out var id) &&
                id.GetString() is { Length: > 0 } existingModelId)
            {
                logger.LogInformation("Using existing OpenFGA authorization model {ModelId}.", existingModelId);
                return existingModelId;
            }
        }

        using var writeRequest = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/authorization-models");
        writeRequest.Content = new StringContent(OpenFgaModel.Json, Encoding.UTF8, "application/json");
        using var writeResponse = await client.SendAsync(writeRequest, cancellationToken);
        if (!writeResponse.IsSuccessStatusCode)
        {
            var body = await writeResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OpenFGA model write failed ({(int)writeResponse.StatusCode}): {body}");
        }

        var modelId = await ReadStringPropertyAsync(writeResponse, "authorization_model_id", cancellationToken)
            ?? throw new InvalidOperationException("OpenFGA model write returned no id.");
        logger.LogInformation("Wrote OpenFGA authorization model {ModelId}.", modelId);
        return modelId;
    }

    private async Task WriteBootstrapTuplesAsync(HttpClient client, string storeId, string modelId, CancellationToken cancellationToken)
    {
        var tuples = new List<(string User, string Relation, string Object)>
        {
            ($"group:{_options.BootstrapAdminGroup}#member", AuthConstants.AdminRelation, AuthConstants.SystemObject),
            ("group:users#member", AuthConstants.MemberRelation, AuthConstants.SystemObject),
            ("group:viewers#member", AuthConstants.ViewerRelation, AuthConstants.SystemObject),
            ("group:owner#member", AuthConstants.OwnerRelation, AuthConstants.SystemObject)
        };

        foreach (var subject in SplitSubjects(_options.BootstrapOwnerSubjects))
        {
            tuples.Add(($"user:{subject}", AuthConstants.OwnerRelation, AuthConstants.SystemObject));
        }

        foreach (var (user, relation, @object) in tuples)
        {
            await WriteTupleAsync(client, storeId, modelId, user, relation, @object, cancellationToken);
        }
    }

    private async Task WriteTupleAsync(
        HttpClient client,
        string storeId,
        string modelId,
        string user,
        string relation,
        string @object,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, $"/stores/{Uri.EscapeDataString(storeId)}/write");
        request.Content = JsonContent.Create(new
        {
            authorization_model_id = modelId,
            writes = new { tuple_keys = new[] { new { user, relation, @object } } }
        }, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Bootstrap tuple written: {Object}#{Relation}@{User}.", @object, relation, user);
            return;
        }

        // OpenFGA returns 400 with code `write_failed_due_to_invalid_input` when the tuple already
        // exists; that is the expected idempotent outcome on subsequent startups.
        logger.LogDebug(
            "Bootstrap tuple skipped ({StatusCode}): {Object}#{Relation}@{User}.",
            (int)response.StatusCode,
            @object,
            relation,
            user);
    }

    private static async Task<string?> ReadStringPropertyAsync(HttpResponseMessage response, string property, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
    }

    private static IEnumerable<string> SplitSubjects(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
