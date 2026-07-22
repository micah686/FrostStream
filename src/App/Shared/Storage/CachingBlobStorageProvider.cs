using System.Collections.Concurrent;
using FluentStorage.Storage;

namespace Shared.Storage;

public sealed class CachingBlobStorageProvider(IStorageConfigClient storageConfigClient) : IBlobStorageProvider
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IStore>>> _cache = new(StringComparer.Ordinal);

    public Task<IStore> GetAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var lazy = _cache.GetOrAdd(storageKey, key => new Lazy<Task<IStore>>(
            () => BuildAsync(key, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    public void Invalidate(string storageKey)
    {
        if (_cache.TryRemove(storageKey, out var removed) && removed.IsValueCreated)
        {
            // Best-effort dispose of the previous instance once it resolves.
            _ = DisposeWhenReadyAsync(removed.Value);
        }
    }

    private async Task<IStore> BuildAsync(string storageKey, CancellationToken cancellationToken)
    {
        var config = await storageConfigClient.GetStorageConfigAsync(storageKey, cancellationToken).ConfigureAwait(false);
        return FluentStorageProvider.CreateStorage(config);
    }

    private static async Task DisposeWhenReadyAsync(Task<IStore> task)
    {
        try
        {
            var storage = await task.ConfigureAwait(false);
            storage.Dispose();
        }
        catch
        {
            // Builder failed; nothing to dispose.
        }
    }
}
