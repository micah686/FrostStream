using Conduit.NATS;

namespace Shared.Messaging;

/// <summary>
/// Server-side browser authentication state. DataBridge provisions this bucket; WebAPI only
/// consumes it so browser sessions never introduce a PostgreSQL dependency into the API.
/// </summary>
public sealed class AuthSessionsTopology : ITopologySource
{
    public const string BucketNameValue = "FROSTSTREAM_AUTH_SESSIONS";

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
