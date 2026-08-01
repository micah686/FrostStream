namespace Conduit.NATS;

/// <summary>
/// A source of declarative topology specifications (streams, consumers, KV buckets, object stores).
/// Implement this to describe an application's NATS topology; provisioning is idempotent.
/// </summary>
public interface ITopologySource
{
    /// <summary>Stream specifications to provision.</summary>
    IEnumerable<StreamSpec> GetStreams();

    /// <summary>Consumer specifications to provision.</summary>
    IEnumerable<ConsumerSpec> GetConsumers();

    /// <summary>KV bucket specifications to provision. Defaults to none.</summary>
    IEnumerable<BucketSpec> GetBuckets() => Enumerable.Empty<BucketSpec>();

    /// <summary>Object store specifications to provision. Defaults to none.</summary>
    IEnumerable<ObjectStoreSpec> GetObjectStores() => Enumerable.Empty<ObjectStoreSpec>();
}
