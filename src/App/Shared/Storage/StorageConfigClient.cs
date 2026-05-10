using FlySwattr.NATS.Abstractions;
using Shared.Messaging;
using Shared.Secrets;

namespace Shared.Storage;

public interface IStorageConfigClient
{
    Task<StorageConfigResponse> GetStorageConfigAsync(string storageKey, CancellationToken cancellationToken = default);
}

public sealed class NatsStorageConfigClient(IMessageBus messageBus, ISecretStore secretStore) : IStorageConfigClient
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

        var stored = ResolveStored(response.Entity);
        if (stored is null)
        {
            return StorageConfigResponse.NotFound(storageKey);
        }

        var secrets = await secretStore.ReadAsync(SecretPaths.ForStorage(storageKey), cancellationToken)
            .ConfigureAwait(false);
        var hydrated = StorageSecretSplitter.Hydrate(stored, secrets);

        return new StorageConfigResponse(
            Found: true,
            Key: response.Entity.Key,
            Method: response.Entity.Method,
            Parameters: StorageParametersSerializer.Serialize(response.Entity.Method, hydrated),
            Description: response.Entity.Description);
    }

    private static StorageParametersStoredBase? ResolveStored(StorageConfigDto entity)
    {
        return entity.Method switch
        {
            StorageMethod.Local when entity.Local is not null => entity.Local,
            StorageMethod.Network when entity.Network is not null => entity.Network,
            StorageMethod.ObjectStorage when entity.ObjectS3Compatible is not null => entity.ObjectS3Compatible,
            StorageMethod.ObjectStorage when entity.ObjectAzureBlob is not null => entity.ObjectAzureBlob,
            StorageMethod.ObjectStorage when entity.ObjectGoogleCloudStorage is not null => entity.ObjectGoogleCloudStorage,
            _ => null
        };
    }
}
