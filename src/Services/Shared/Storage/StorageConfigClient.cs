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
            Parameters: response.Entity.Method switch
            {
                StorageMethod.Local when response.Entity.Local is not null
                    => StorageParametersSerializer.Serialize(response.Entity.Method, response.Entity.Local),
                StorageMethod.Network when response.Entity.Network is not null
                    => StorageParametersSerializer.Serialize(response.Entity.Method, response.Entity.Network),
                StorageMethod.ObjectStorage when response.Entity.ObjectS3Compatible is not null
                    => StorageParametersSerializer.Serialize(response.Entity.Method, response.Entity.ObjectS3Compatible),
                StorageMethod.ObjectStorage when response.Entity.ObjectAzureBlob is not null
                    => StorageParametersSerializer.Serialize(response.Entity.Method, response.Entity.ObjectAzureBlob),
                StorageMethod.ObjectStorage when response.Entity.ObjectGoogleCloudStorage is not null
                    => StorageParametersSerializer.Serialize(response.Entity.Method, response.Entity.ObjectGoogleCloudStorage),
                _ => null
            },
            Description: response.Entity.Description);
    }
}
