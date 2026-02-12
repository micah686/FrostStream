using Shared.Messages;

namespace Shared.Storage;

/// <summary>
/// Client for requesting storage configurations from DataBridge via NATS.
/// </summary>
public interface IStorageConfigClient
{
    Task<StorageConfigResponse> GetStorageConfigAsync(string storageKey, CancellationToken cancellationToken = default);
}
