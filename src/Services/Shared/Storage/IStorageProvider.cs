using FluentStorage.Blobs;

namespace Shared.Storage;

/// <summary>
/// Interface for obtaining IBlobStorage instances based on a storage configuration key.
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Gets a blob storage instance for the specified storage key.
    /// </summary>
    /// <param name="storageKey">The storage configuration key (e.g., "default", "premium-tier").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured IBlobStorage instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the storage configuration is not found.</exception>
    Task<IBlobStorage> GetStorageAsync(string storageKey, CancellationToken cancellationToken = default);
}
