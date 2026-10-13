using Conduit.NATS;
using Shared.Messaging;
using static Shared.Messaging.AuthSessionsTopology;

namespace FrostStream.Full.Infrastructure;

public sealed class NatsAuthSessionsTopology : ITopologySource
{
    public IEnumerable<StreamSpec> GetStreams() => [];

    public IEnumerable<ConsumerSpec> GetConsumers() => [];

    public IEnumerable<BucketSpec> GetBuckets()
    {
        yield return new BucketSpec
        {
            Name = BucketName.From(BucketNameValue),
            StorageType = StorageType.File,
            History = 1,
            MaxAge = TimeSpan.FromDays(31),
            MaxBytes = 512L * 1024 * 1024,
            Replicas = 1,
            Description = "Encrypted FrostStream browser authentication tickets and refresh leases"
        };
    }
}
