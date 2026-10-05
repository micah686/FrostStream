using DataBridge;
using DataBridge.Messaging;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Deployment;

namespace Lite;

// The host resolves all hosted services before starting any of them. Initialize the
// baseline in the first registration's constructor before workflow stores are resolved.
public sealed class LiteSchemaInitializationService : IHostedService
{
    public LiteSchemaInitializationService(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DataBridge.Persistence.IApplicationSchemaInitializer>().Initialize();
        services.GetRequiredService<DataBridge.Persistence.Workflows.SqliteFunctionStore>().Initialize().GetAwaiter().GetResult();
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class LiteHandlerInitializationService(IServiceProvider services, ApplicationStartupGate gate,
    DownloadFlowStartupState reconciliation) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!reconciliation.IsReady)
            throw new InvalidOperationException("Lite handler initialization requires completed startup reconciliation.");
        await Task.WhenAll(services.GetServices<IHostedService>().TakeWhile(service => !ReferenceEquals(service, this)).OfType<IApplicationHandlerInitialization>()
            .Select(service => service.RegistrationCompleted)).WaitAsync(cancellationToken);
        gate.Release();
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class LiteReadinessState
{
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    internal void SetReady(bool value) => Volatile.Write(ref ready, value ? 1 : 0);
}

public sealed class LiteReadinessService(LiteReadinessState state) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.SetReady(true);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        state.SetReady(false);
        return Task.CompletedTask;
    }
}

public sealed class LiteReadinessHealthCheck(LiteReadinessState state, IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(state.IsReady && !lifetime.ApplicationStopping.IsCancellationRequested
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Lite initialization has not completed or the host is stopping."));
}
