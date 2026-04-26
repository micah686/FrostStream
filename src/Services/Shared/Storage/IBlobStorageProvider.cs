using FluentStorage.Blobs;

namespace Shared.Storage;

/// <summary>
/// Resolves an <see cref="IBlobStorage"/> for a given storage key, fetching the
/// non-sensitive config from DataBridge and the sensitive credentials from the
/// secret store, then merging them into a FluentStorage connection string.
/// Resulting <see cref="IBlobStorage"/> instances are cached per storage key
/// and invalidated by <c>StorageConfigChanged</c> events.
/// </summary>
public interface IBlobStorageProvider
{
    Task<IBlobStorage> GetAsync(string storageKey, CancellationToken cancellationToken = default);

    void Invalidate(string storageKey);
}
