using FlySwattr.NATS.Abstractions;
using Shared.Messages;

namespace Shared.Storage;

public class NatsStorageConfigClient : IStorageConfigClient
{
    private readonly IMessageBus _messageBus;

    public NatsStorageConfigClient(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }

    public async Task<StorageConfigResponse> GetStorageConfigAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var response = await _messageBus.RequestAsync<StorageConfigRequest, StorageConfigResponse>(
            Subjects.StorageConfig,
            new StorageConfigRequest(storageKey),
            TimeSpan.FromSeconds(10),
            cancellationToken);

        return response ?? new StorageConfigResponse(
            Found: false,
            Key: null,
            Method: null,
            Parameters: null,
            Description: null);
    }
}
