using FluentStorage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Shared.Messages;
using Shared.Storage;

namespace WebAPI.HealthChecks;

/// <summary>
/// Health check that verifies storage connectivity by attempting to 
/// retrieve the default storage config and perform a basic operation.
/// </summary>
public class StorageHealthCheck : IHealthCheck
{
    private readonly IStorageConfigClient _storageConfigClient;
    private readonly ILogger<StorageHealthCheck> _logger;

    public StorageHealthCheck(
        IStorageConfigClient storageConfigClient,
        ILogger<StorageHealthCheck> logger)
    {
        _storageConfigClient = storageConfigClient ?? throw new ArgumentNullException(nameof(storageConfigClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Try to get default storage config
            var config = await _storageConfigClient.GetStorageConfigAsync("default", cancellationToken);
            if (!config.Found)
            {
                return HealthCheckResult.Degraded("Default storage config not found");
            }

            // Try to create storage and perform a basic operation
            var storage = FluentStorageProvider.CreateStorage(config);

            // Try to list root (lightweight operation)
            var items = await storage.ListAsync(new ListOptions { FolderPath = "" }, cancellationToken);
            _ = items.Count(); // Materialize to ensure the operation completes

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storage health check failed");
            return HealthCheckResult.Unhealthy("Storage connectivity failed", ex);
        }
    }
}
