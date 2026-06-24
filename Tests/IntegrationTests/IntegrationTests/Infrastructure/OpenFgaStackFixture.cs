using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Auth;
using WebAPI.Auth;

namespace IntegrationTests.Infrastructure;

/// <summary>
/// Real OpenFGA stack (in-memory datastore) for end-to-end verification of the Axis 1 API-surface
/// authorization model. Runs the production <see cref="OpenFgaProvisioner"/> to write the model and
/// seed the registry-generated tuples, then exposes the real <see cref="OpenFgaAuthorizer"/>,
/// <see cref="OpenFgaTupleWriter"/>, and <see cref="OpenFgaBundleManagementService"/> pointed at the
/// live store.
/// </summary>
public sealed class OpenFgaStackFixture : IAsyncDisposable
{
    public const string AdminGroup = "admins";
    public const string OwnerSubject = "owner-smoke";

    // No in-container port wait strategy: the openfga image's minimal shell makes UntilPortIsAvailable
    // unreliable. We wait from the host against /healthz on the mapped port (WaitForOpenFgaAsync).
    private readonly IContainer _openFgaContainer = new ContainerBuilder()
        .WithImage("openfga/openfga:latest")
        .WithCommand("run")
        .WithPortBinding(8080, true)
        .Build();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServiceProvider? _services;
    private bool _initialized;

    public string Endpoint => $"http://127.0.0.1:{_openFgaContainer.GetMappedPublicPort(8080)}";

    public OpenFgaAuthorizer Authorizer => _services!.GetRequiredService<OpenFgaAuthorizer>();

    public IOpenFgaTupleWriter TupleWriter => _services!.GetRequiredService<OpenFgaTupleWriter>();

    public IBundleManagementService Management => _services!.GetRequiredService<OpenFgaBundleManagementService>();

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _openFgaContainer.StartAsync();
                    break;
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(1000);
                }
            }

            await WaitForOpenFgaAsync();
            BuildServices();
            await ProvisionAsync();
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Convenience: does <paramref name="subject"/> pass the OpenFGA <c>invoke</c> check on the endpoint?</summary>
    public async Task<bool> CanInvokeAsync(string subject, string endpointId)
    {
        var decision = await Authorizer.CheckAsync(new FrostStreamAuthorizationCheck(
            $"user:{subject}",
            AuthConstants.InvokeRelation,
            AuthConstants.EndpointObject(endpointId)));
        return decision.Allowed;
    }

    private void BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(Options.Create(new OpenFgaOptions
        {
            Endpoint = Endpoint,
            StoreName = "froststream-smoke",
            AutoProvision = true,
            BootstrapAdminGroup = AdminGroup,
            BootstrapOwnerSubjects = OwnerSubject
        }));
        services.AddSingleton(new OpenFgaRuntimeState());
        services.AddHttpClient(OpenFgaProvisioner.HttpClientName);
        services.AddHttpClient<OpenFgaAuthorizer>();
        services.AddHttpClient<OpenFgaTupleWriter>();
        services.AddHttpClient<OpenFgaBundleManagementService>();

        _services = services.BuildServiceProvider();
    }

    private async Task ProvisionAsync()
    {
        var services = _services!;
        var provisioner = new OpenFgaProvisioner(
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<IOptions<OpenFgaOptions>>(),
            services.GetRequiredService<OpenFgaRuntimeState>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<OpenFgaProvisioner>());

        await ((IHostedService)provisioner).StartAsync(CancellationToken.None);

        // The provisioner seeds in the background; wait until the :all bundle + its admin grant land.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var bundles = await Management.ListBundlesAsync(CancellationToken.None);
            if (bundles.Status == BundleOpStatus.Ok &&
                bundles.Value!.Any(b => b.Id == AuthConstants.AllBundle &&
                                        b.Grants.Any(g => g is { Type: "group", Id: AdminGroup })))
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("OpenFGA provisioning did not complete (the :all bundle was not seeded).");
    }

    private async Task WaitForOpenFgaAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(Endpoint) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                var response = await http.GetAsync("/healthz");
                if ((int)response.StatusCode is >= 200 and < 500)
                {
                    return;
                }
            }
            catch
            {
                // not ready yet
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("OpenFGA container did not become reachable in time.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _openFgaContainer.DisposeAsync();
    }
}
