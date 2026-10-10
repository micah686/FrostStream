using Microsoft.Extensions.Logging;
using NATS.Client.ObjectStore;

namespace Conduit.NATS;

/// <summary>
/// Thin per-bucket passthrough over a NATS Object Store. Lazily resolves the underlying store and
/// caches it; transient resolution is the caller's concern via retries on the operation.
/// </summary>
internal sealed class NatsObjectStore : IObjectStore
{
    private readonly INatsObjContext _objContext;
    private readonly string _bucket;
    private readonly ILogger<NatsObjectStore> _logger;
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private INatsObjStore? _cachedStore;

    public NatsObjectStore(INatsObjContext objContext, string bucket, ILogger<NatsObjectStore> logger)
    {
        _objContext = objContext;
        _bucket = bucket;
        _logger = logger;
    }

    private async Task<INatsObjStore> GetStoreAsync(CancellationToken cancellationToken)
    {
        var store = Volatile.Read(ref _cachedStore);
        if (store != null)
            return store;

        await _storeLock.WaitAsync(cancellationToken);
        try
        {
            store = Volatile.Read(ref _cachedStore);
            if (store != null)
                return store;

            store = await _objContext.GetObjectStoreAsync(_bucket, cancellationToken: cancellationToken);
            Volatile.Write(ref _cachedStore, store);
            return store;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<string> PutAsync(string key, Stream data, CancellationToken cancellationToken = default)
    {
        var store = await GetStoreAsync(cancellationToken);
        var meta = await store.PutAsync(key, data, leaveOpen: true, cancellationToken: cancellationToken);
        return meta.Name;
    }

    public async Task GetAsync(string key, Stream target, CancellationToken cancellationToken = default)
    {
        var store = await GetStoreAsync(cancellationToken);
        await store.GetAsync(key, target, cancellationToken: cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var store = await GetStoreAsync(cancellationToken);
        try
        {
            await store.DeleteAsync(key, cancellationToken: cancellationToken);
        }
        catch (NatsObjNotFoundException)
        {
            _logger.LogDebug("Object {Key} not found in bucket {Bucket} during delete (idempotent)", key, _bucket);
        }
    }

    public ValueTask DisposeAsync()
    {
        _storeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
