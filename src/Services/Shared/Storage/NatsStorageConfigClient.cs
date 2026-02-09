using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared.Messages;

namespace Shared.Storage;

/// <summary>
/// Implementation of <see cref="IStorageConfigClient"/> that uses NATS request/reply
/// to retrieve storage configurations from DataBridge.
/// </summary>
public class NatsStorageConfigClient : IStorageConfigClient
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<NatsStorageConfigClient> _logger;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public NatsStorageConfigClient(IMessageBus messageBus, ILogger<NatsStorageConfigClient> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<StorageConfigResponse?> GetConfigAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Requesting storage config for key '{StorageKey}'", storageKey);

        var request = new StorageConfigRequest { StorageKey = storageKey };
        var response = await _messageBus.RequestAsync<StorageConfigRequest, StorageConfigResponse>(
            Subjects.StorageConfig,
            request,
            DefaultTimeout,
            cancellationToken);

        if (response is null)
        {
            _logger.LogWarning("No response received for storage config key '{StorageKey}'", storageKey);
        }
        else
        {
            _logger.LogDebug("Received storage config for key '{StorageKey}': Method={Method}", storageKey, response.Method);
        }

        return response;
    }
}
