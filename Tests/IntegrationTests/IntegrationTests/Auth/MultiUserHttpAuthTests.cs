using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using FlySwattr.NATS.Abstractions;
using IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shared.Auth;
using Shared.Messaging;
using Shared.Secrets;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;
using WebAPI.Features.Auth.Controllers;

namespace IntegrationTests.Auth;

/// <summary>
/// Drives the full multi-user HTTP request pipeline (real <c>JwtBearer</c> authentication → claims →
/// dynamic <see cref="EndpointPolicyProvider"/> → <see cref="FrostStreamPermissionHandler"/> → a real
/// OpenFGA <c>invoke</c> check) end-to-end through <see cref="WebApplicationFactory{TEntryPoint}"/>.
///
/// Tokens are self-issued RS256 JWTs validated against a test signing key the factory injects into
/// <see cref="JwtBearerOptions"/> — no Authentik instance is required. Authorization runs against a
/// real OpenFGA testcontainer (provisioned by the production <see cref="OpenFgaProvisioner"/> via the
/// shared <see cref="OpenFgaStackFixture"/>); deny/allow is driven by writing real group tuples.
/// </summary>
public class MultiUserHttpAuthTests
{
    private static readonly OpenFgaStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MultiUserWebApiFactory? _factory;

    static MultiUserHttpAuthTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _factory?.Dispose();
            Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
    }

    [Before(Test)]
    public async Task SetupAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
        _factory ??= new MultiUserWebApiFactory(Fixture.Endpoint, Fixture.StoreId, Fixture.ModelId);
    }

    [After(Test)]
    public void Release() => Gate.Release();

    [Test]
    public async Task Anonymous_Request_To_A_Protected_Route_Is_401()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync("/api/metadata");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Authenticated_But_Ungranted_Subject_Is_403()
    {
        using var client = _factory!.CreateClient();
        // A valid token whose subject has no OpenFGA grant of any kind.
        Authorize(client, _factory.Tokens.Issue($"nobody-{Guid.NewGuid():N}"));

        var response = await client.GetAsync("/api/metadata");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Subject_In_The_Admins_Group_Is_200()
    {
        var subject = $"admin-{Guid.NewGuid():N}";
        // Reconcile the subject into the seeded `admins` group, which is granted the :all bundle.
        await Fixture.TupleWriter.SyncUserGroupsAsync(subject, [OpenFgaStackFixture.AdminGroup]);

        using var client = _factory!.CreateClient();
        Authorize(client, _factory.Tokens.Issue(subject, OpenFgaStackFixture.AdminGroup));

        var response = await client.GetAsync("/api/metadata");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Auth_Config_Is_Anonymous_And_Reports_Multi_User_Mode()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AuthConfigResponse>();
        body.ShouldNotBeNull();
        body!.Mode.ShouldBe("multi-user");
    }

    [Test]
    public async Task Liveness_Endpoint_Is_Anonymous()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync("/alive");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static void Authorize(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

/// <summary>
/// Hosts <see cref="global::WebAPI.Program"/> in multi-user mode pointed at the live OpenFGA store,
/// swaps NATS/secrets for in-process fakes, and validates RS256 tokens against an injected test key.
/// </summary>
internal sealed class MultiUserWebApiFactory(string openFgaEndpoint, string storeId, string modelId)
    : WebApplicationFactory<global::WebAPI.Program>
{
    public TestTokenIssuer Tokens { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Turn single-user mode OFF for this test host only — the same way AppHost.cs flips it when
        // launched with SINGLE_USER_MODE=false: it stamps the SINGLE_USER_MODE master switch and the
        // derived Auth__SingleUserMode flag onto the WebAPI process (AuthMode checks both). We set the
        // equivalent host settings here instead of real environment variables on purpose: this stays
        // scoped to THIS WebApplicationFactory, so it never leaks into the single-user integration
        // tests sharing this process, and normal `dotnet run` / AppHost dev use keeps single-user on
        // (appsettings.Development.json still defaults Auth:SingleUserMode=true).
        builder.UseSetting("SINGLE_USER_MODE", "false");
        builder.UseSetting("Auth:SingleUserMode", "false");

        // A non-blank authority is required by Program; the test token issuer overrides JwtBearer so
        // the authority is never actually contacted.
        builder.UseSetting("Auth:Authority", "https://authentik.test/application/o/froststream/");
        builder.UseSetting("Auth:Audience", TestTokenIssuer.Audience);
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");

        // Point the real OpenFgaAuthorizer at the provisioned testcontainer store; disable the
        // in-app provisioner (the fixture already provisioned the store/model/tuples).
        builder.UseSetting("OpenFga:Endpoint", openFgaEndpoint);
        builder.UseSetting("OpenFga:StoreId", storeId);
        builder.UseSetting("OpenFga:AuthorizationModelId", modelId);
        builder.UseSetting("OpenFga:AutoProvision", "false");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IMessageBus>();
            services.RemoveAll<ISecretStore>();

            services.AddSingleton<IMessageBus>(new MetadataStubBus());
            services.AddSingleton<ISecretStore>(new InMemorySecretStore());

            // Validate tokens against the test signing key instead of fetching Authentik's JWKS.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, Tokens.Configure);
        });
    }
}

/// <summary>Mints and validates RS256 access tokens with an ephemeral in-test RSA key.</summary>
internal sealed class TestTokenIssuer
{
    public const string Audience = "froststream-api";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "froststream-test-key" };

    public void Configure(JwtBearerOptions options)
    {
        options.RequireHttpsMetadata = false;
        // A static configuration short-circuits the metadata/JWKS fetch entirely.
        options.Configuration = new OpenIdConnectConfiguration();
        options.Configuration.SigningKeys.Add(_key);

        var parameters = options.TokenValidationParameters;
        parameters.ValidateIssuer = false;
        parameters.ValidateIssuerSigningKey = true;
        parameters.IssuerSigningKey = _key;
        // Audience validation (ValidAudiences) is configured by Program and left intact.
    }

    public string Issue(string subject, params string[] groups)
    {
        var claims = new List<Claim> { new(AuthConstants.SubjectClaim, subject) };
        claims.AddRange(groups.Select(group => new Claim(AuthConstants.GroupsClaim, group)));

        var token = new JwtSecurityToken(
            issuer: "https://authentik.test/application/o/froststream/",
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>
/// Minimal in-process bus: answers the metadata-list query so the authorized (200) path returns a
/// real result, and no-ops everything else.
/// </summary>
internal sealed class MetadataStubBus : IMessageBus
{
    public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync<T>(
        string subject,
        Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ISubscription>(new NoopSubscription());

    public Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (subject == MetadataSubjects.List)
        {
            return Task.FromResult((TResponse?)(object)new MetadataListResponseMessage
            {
                Success = true,
                Items = [],
                Page = 1,
                TotalCount = 0,
                HasMore = false
            });
        }

        return Task.FromResult<TResponse?>(default);
    }

    private sealed class NoopSubscription : ISubscription
    {
        public Guid Id { get; } = Guid.NewGuid();

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
