namespace Conduit.NATS;

/// <summary>
/// Creates or updates NATS JetStream topology (streams, consumers, KV buckets, object stores).
/// All operations are idempotent.
/// </summary>
public interface ITopologyManager
{
    Task EnsureStreamAsync(StreamSpec spec, CancellationToken cancellationToken = default);
    Task EnsureConsumerAsync(ConsumerSpec spec, CancellationToken cancellationToken = default);
    Task EnsureBucketAsync(BucketName name, StorageType storageType, CancellationToken cancellationToken = default);
    Task EnsureBucketAsync(BucketSpec spec, CancellationToken cancellationToken = default);
    Task EnsureObjectStoreAsync(BucketName name, StorageType storageType, CancellationToken cancellationToken = default);
    Task EnsureObjectStoreAsync(ObjectStoreSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordination primitive that lets dependent services block until topology provisioning completes.
/// </summary>
public interface ITopologyReadySignal
{
    /// <summary>Completes when topology is ready; throws if provisioning failed.</summary>
    Task WaitAsync(CancellationToken cancellationToken = default);

    /// <summary>Signals successful completion. Thread-safe; subsequent calls are ignored.</summary>
    void SignalReady();

    /// <summary>Signals unrecoverable failure; waiters observe <paramref name="exception"/>.</summary>
    void SignalFailed(Exception exception);

    /// <summary>Whether the signal has been set (ready or failed).</summary>
    bool IsSignaled { get; }
}
