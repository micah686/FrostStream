using FluentStorage;
using FluentStorage.Blobs;
using Microsoft.Extensions.Logging;

namespace Shared.Storage;

/// <summary>
/// Implementation of <see cref="IStorageProvider"/> that uses FluentStorage
/// and retrieves configurations via <see cref="IStorageConfigClient"/>.
/// </summary>
public class FluentStorageProvider : IStorageProvider
{
    private readonly IStorageConfigClient _configClient;
    private readonly ILogger<FluentStorageProvider> _logger;

    public FluentStorageProvider(IStorageConfigClient configClient, ILogger<FluentStorageProvider> logger)
    {
        _configClient = configClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IBlobStorage> GetStorageAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting storage for key '{StorageKey}'", storageKey);

        var config = await _configClient.GetConfigAsync(storageKey, cancellationToken);

        if (config is null)
        {
            throw new InvalidOperationException($"Storage configuration '{storageKey}' not found. Ensure DataBridge is running and the configuration exists in the database.");
        }

        _logger.LogInformation("Creating storage with method {Method} for key '{StorageKey}'", config.Method, storageKey);

        return StorageFactory.Blobs.FromConnectionString(config.ConnectionString);
    }
}
