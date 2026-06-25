using System.Net;
using System.Text;
using Shared.Auth;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

/// <summary>
/// Records every outgoing request (method, uri, body) and returns whatever the supplied responder
/// produces. Lets the OpenFGA HTTP clients be unit-tested without a real store.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<RecordedRequest, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body);
        Requests.Add(recorded);
        return responder(recorded);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body)
{
    public string Path => Uri.AbsolutePath;
}

/// <summary>Hands the same client back regardless of the requested name.</summary>
internal sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

/// <summary>Captures the last check it was asked to evaluate and returns a fixed decision.</summary>
internal sealed class RecordingAuthorizer(FrostStreamAuthorizationDecision decision) : IFrostStreamAuthorizer
{
    public FrostStreamAuthorizationCheck? LastCheck { get; private set; }

    public Task<FrostStreamAuthorizationDecision> CheckAsync(
        FrostStreamAuthorizationCheck check,
        CancellationToken cancellationToken = default)
    {
        LastCheck = check;
        return Task.FromResult(decision);
    }
}

/// <summary>Captures the subject/groups a sync was invoked with.</summary>
internal sealed class RecordingTupleWriter : IOpenFgaTupleWriter
{
    public string? Subject { get; private set; }
    public IReadOnlyCollection<string>? Groups { get; private set; }
    public int Calls { get; private set; }

    public Task SyncUserGroupsAsync(string subject, IReadOnlyCollection<string> groups, CancellationToken cancellationToken = default)
    {
        Subject = subject;
        Groups = groups;
        Calls++;
        return Task.CompletedTask;
    }
}
