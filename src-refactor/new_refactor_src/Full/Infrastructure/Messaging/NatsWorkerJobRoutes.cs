using FrostStream.ApplicationContracts;

namespace Shared.Messaging.Adapters;

public sealed class NatsWorkerJobRoutes(Conduit.NATS.ITopologyManager topology) : IWorkerJobRoutes
{
    public Task EnsureTaggedConsumerAsync(string stream, string consumer, string subject, string tag, CancellationToken cancellationToken = default)
    {
        var spec = stream switch
        {
            DownloadTopology.StreamNameValue => FrostStream.Full.Infrastructure.NatsDownloadTopology.TaggedWorkerConsumerSpec(consumer, subject, tag),
            ArtifactStorageTopology.StreamNameValue => FrostStream.Full.Infrastructure.NatsArtifactStorageTopology.TaggedWorkerConsumerSpec(consumer, subject, tag),
            LocalImportTopology.StreamNameValue => FrostStream.Full.Infrastructure.NatsLocalImportTopology.TaggedWorkerConsumerSpec(consumer, subject, tag),
            _ => throw new ArgumentException($"Unknown worker stream '{stream}'.", nameof(stream))
        };
        return topology.EnsureConsumerAsync(spec, cancellationToken);
    }
}
