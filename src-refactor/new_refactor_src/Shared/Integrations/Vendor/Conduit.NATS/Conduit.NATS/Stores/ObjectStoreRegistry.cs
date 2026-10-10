using System.Collections.Concurrent;

namespace Conduit.NATS;

/// <summary>Caches one <see cref="IObjectStore"/> instance per bucket name, created via the injected factory.</summary>
internal sealed class ObjectStoreRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IObjectStore> _stores = new(StringComparer.Ordinal);
    private readonly Func<string, IObjectStore> _factory;

    public ObjectStoreRegistry(Func<string, IObjectStore> factory) => _factory = factory;

    public IObjectStore GetOrCreate(string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        return _stores.GetOrAdd(bucket, _factory);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores.Values)
            await store.DisposeAsync().ConfigureAwait(false);
        _stores.Clear();
    }
}
