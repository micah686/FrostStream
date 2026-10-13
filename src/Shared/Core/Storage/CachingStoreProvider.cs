using System.Collections.Concurrent;
using FluentStorage.Storage;

namespace Shared.Storage;

public sealed class CachingStoreProvider(IStorageConfigClient storageConfigClient) : IStoreProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IStore>>> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<IStore> GetAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lazy = _cache.GetOrAdd(storageKey, key => new Lazy<Task<IStore>>(
            () => BuildAsync(key),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // Store construction is shared by every caller for this key. A caller may cancel its
            // own wait without poisoning the cached construction for other callers.
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (lazy.IsValueCreated && (lazy.Value.IsFaulted || lazy.Value.IsCanceled))
        {
            RemoveIfCurrent(storageKey, lazy);
            throw;
        }
    }

    public void Invalidate(string storageKey)
    {
        if (_cache.TryRemove(storageKey, out var removed) && removed.IsValueCreated)
        {
            // Best-effort dispose of the previous instance once it resolves.
            _ = DisposeWhenReadyAsync(removed.Value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _cache.ToArray())
        {
            if (_cache.TryRemove(entry.Key, out var removed) && removed.IsValueCreated)
            {
                _ = DisposeWhenReadyAsync(removed.Value);
            }
        }

        GC.SuppressFinalize(this);
    }

    private async Task<IStore> BuildAsync(string storageKey)
    {
        var config = await storageConfigClient.GetStorageConfigAsync(storageKey, CancellationToken.None).ConfigureAwait(false);
        return FluentStoreFactory.CreateStorage(config);
    }

    private void RemoveIfCurrent(string storageKey, Lazy<Task<IStore>> expected)
    {
        if (_cache.TryGetValue(storageKey, out var current) && ReferenceEquals(current, expected))
        {
            _cache.TryRemove(storageKey, out _);
        }
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
