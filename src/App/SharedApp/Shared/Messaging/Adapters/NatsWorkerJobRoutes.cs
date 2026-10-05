using FrostStream.ApplicationContracts;

namespace Shared.Messaging.Adapters;

public sealed class NatsWorkerJobRoutes(Conduit.NATS.ITopologyManager topology) : IWorkerJobRoutes
{
    public Task EnsureTaggedConsumerAsync(string stream, string consumer, string subject, string tag, CancellationToken cancellationToken = default)
    {
        var spec = stream switch
        {
            DownloadTopology.StreamNameValue => DownloadTopology.TaggedWorkerConsumerSpec(consumer, subject, tag),
            ArtifactStorageTopology.StreamNameValue => ArtifactStorageTopology.TaggedWorkerConsumerSpec(consumer, subject, tag),
            LocalImportTopology.StreamNameValue => LocalImportTopology.TaggedWorkerConsumerSpec(consumer, subject, tag),
            _ => throw new ArgumentException($"Unknown worker stream '{stream}'.", nameof(stream))
        };
        return topology.EnsureConsumerAsync(spec, cancellationToken);
    }
}
