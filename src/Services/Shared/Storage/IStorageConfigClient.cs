using Shared.Messages;

namespace Shared.Storage;

/// <summary>
/// Interface for requesting storage configurations via NATS request/reply.
/// </summary>
public interface IStorageConfigClient
{
    /// <summary>
    /// Gets the storage configuration for the specified key.
    /// </summary>
    /// <param name="storageKey">The storage configuration key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The storage configuration response, or null if not found.</returns>
    Task<StorageConfigResponse?> GetConfigAsync(string storageKey, CancellationToken cancellationToken = default);
}
