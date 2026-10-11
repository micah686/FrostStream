using DataBridge;
using DataBridge.Messaging;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Deployment;

namespace Lite;

/// <summary>Runs after every hosted service has registered its handlers, independent of descriptor order.</summary>
public sealed class LiteRuntimeLifecycle(IServiceProvider services, ApplicationStartupGate gate,
    DownloadFlowStartupState reconciliation, LiteReadinessState readiness) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StartedAsync(CancellationToken ct)
    {
        if (!reconciliation.IsReady) throw new InvalidOperationException("Download startup reconciliation is incomplete.");
        await Task.WhenAll(services.GetServices<IHostedService>().OfType<IApplicationHandlerInitialization>()
            .Select(handler => handler.RegistrationCompleted)).WaitAsync(ct);
        gate.Release();
        foreach (var step in services.GetServices<IApplicationReadyStep>()) await step.StartAsync(ct);
        ct.ThrowIfCancellationRequested(); readiness.SetReady(true);
    }
    public Task StoppingAsync(CancellationToken ct) { readiness.SetReady(false); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class LiteReadinessState
{
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    internal void SetReady(bool value) => Volatile.Write(ref ready, value ? 1 : 0);
}

public sealed class LiteReadinessHealthCheck(LiteReadinessState state, IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(state.IsReady && !lifetime.ApplicationStopping.IsCancellationRequested
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Lite initialization has not completed or the host is stopping."));
}
