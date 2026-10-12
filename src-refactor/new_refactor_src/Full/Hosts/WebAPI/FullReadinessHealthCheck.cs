using DataBridge.Search;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Messaging;

namespace FrostStream.Full.WebAPI;

/// <summary>Checks the application request path; search availability is reported separately.</summary>
internal sealed class FullReadinessHealthCheck(IMessageBus bus, IHostApplicationLifetime lifetime) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested) return HealthCheckResult.Unhealthy("Host is stopping.");
        try
        {
            var reply = await bus.RequestAsync<SearchStatusRequest, SearchStatusSnapshot>(SearchSubjects.Status,
                new(), TimeSpan.FromSeconds(3), cancellationToken);
            return reply is null ? HealthCheckResult.Unhealthy("Application handlers are not available.") : HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Application transport is not available.");
        }
    }
}
