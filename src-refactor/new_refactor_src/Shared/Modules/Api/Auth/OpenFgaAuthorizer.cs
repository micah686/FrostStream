using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Shared.Auth;

namespace WebAPI.Auth;

public sealed class OpenFgaAuthorizer(
    HttpClient httpClient,
    IOptions<OpenFgaOptions> options,
    OpenFgaRuntimeState state,
    ILogger<OpenFgaAuthorizer> logger) : IFrostStreamAuthorizer
{
    private readonly OpenFgaOptions _options = options.Value;

    public async Task<FrostStreamAuthorizationDecision> CheckAsync(
        FrostStreamAuthorizationCheck check,
        CancellationToken cancellationToken = default)
    {
        var storeId = state.StoreId;
        if (string.IsNullOrWhiteSpace(_options.Endpoint) ||
            string.IsNullOrWhiteSpace(storeId))
        {
            logger.LogWarning("OpenFGA authorization is not configured; denying {Relation} on {Object} for {User}.",
                check.Relation,
                check.Object,
                check.User);
            return FrostStreamAuthorizationDecision.Deny("OpenFGA is not configured.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.Endpoint.TrimEnd('/')}/stores/{Uri.EscapeDataString(storeId)}/check");

        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }

        request.Content = JsonContent.Create(new OpenFgaCheckRequest
        {
            AuthorizationModelId = string.IsNullOrWhiteSpace(state.AuthorizationModelId) ? null : state.AuthorizationModelId,
            TupleKey = new OpenFgaTupleKey
            {
                User = check.User,
                Relation = check.Relation,
                Object = check.Object
            }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "OpenFGA check failed with status {StatusCode}; denying {Relation} on {Object} for {User}.",
                (int)response.StatusCode,
                check.Relation,
                check.Object,
                check.User);
            return FrostStreamAuthorizationDecision.Deny("OpenFGA check failed.");
        }

        var result = await response.Content.ReadFromJsonAsync<OpenFgaCheckResponse>(cancellationToken);
        return result?.Allowed == true
            ? FrostStreamAuthorizationDecision.Permit()
            : FrostStreamAuthorizationDecision.Deny("OpenFGA denied the request.");
    }

    private sealed class OpenFgaCheckRequest
    {
        [JsonPropertyName("authorization_model_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AuthorizationModelId { get; init; }

        [JsonPropertyName("tuple_key")]
        public required OpenFgaTupleKey TupleKey { get; init; }
    }

    private sealed class OpenFgaTupleKey
    {
        public required string User { get; init; }

        public required string Relation { get; init; }

        public required string Object { get; init; }
    }

    private sealed class OpenFgaCheckResponse
    {
        [JsonPropertyName("allowed")]
        public bool Allowed { get; init; }
    }
}
