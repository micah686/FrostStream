using DataBridge.Initialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace FrostStream.Lite;

public sealed class LiteDatabaseReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT version
                FROM froststream.initialization_state
                WHERE component = 'application'
                """);
            var version = await command.ExecuteScalarAsync(cancellationToken);
            return version is int current && current == ApplicationInitializationCoordinator.CurrentVersion
                ? HealthCheckResult.Healthy("Lite database state is initialized and compatible.")
                : HealthCheckResult.Unhealthy(
                    $"Lite database initialization version is {version?.ToString() ?? "missing"}; " +
                    $"required version is {ApplicationInitializationCoordinator.CurrentVersion}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Lite database readiness check failed.", exception);
        }
    }
}
