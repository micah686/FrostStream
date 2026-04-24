using FlySwattr.NATS.Abstractions;
using Shared.Messaging;

namespace Shared.Storage;

public interface IStorageConfigClient
{
    Task<StorageConfigResponse> GetStorageConfigAsync(string storageKey, CancellationToken cancellationToken = default);
}

public sealed class NatsStorageConfigClient(IMessageBus messageBus) : IStorageConfigClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<StorageConfigResponse> GetStorageConfigAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        var response = await messageBus.RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.GetStorage,
            new StorageGetRequestMessage { Key = storageKey },
            RequestTimeout,
            cancellationToken);

        if (response is null)
        {
            return StorageConfigResponse.NotFound(storageKey);
        }

        if (!response.Success)
        {
            if (string.Equals(response.ErrorCode, "not_found", StringComparison.OrdinalIgnoreCase))
            {
                return StorageConfigResponse.NotFound(storageKey);
            }

            throw new InvalidOperationException(
                $"Failed to fetch storage config for key '{storageKey}': {response.ErrorMessage ?? "unknown error"}");
        }

        if (response.Entity is null)
        {
            return StorageConfigResponse.NotFound(storageKey);
        }

        return new StorageConfigResponse(
            Found: true,
            Key: response.Entity.Key,
            Method: response.Entity.Method,
            Parameters: response.Entity.Parameters,
            Description: response.Entity.Description);
    }
}
