using Conduit.NATS;
using Shared.Messaging;
using static Shared.Messaging.PlaylistTopology;

namespace FrostStream.Full.Infrastructure;

public sealed class NatsPlaylistTopology : ITopologySource
{
    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec
        {
            Name = StreamName.From(StreamNameValue),
            Subjects = SubjectFilters,
            MaxAge = TimeSpan.FromDays(30),
            RetentionPolicy = StreamRetention.Limits,
            StorageType = StorageType.File,
            Replicas = 1
        };
    }

    public IEnumerable<ConsumerSpec> GetConsumers()
    {
        yield return DataBridgeConsumer(PlaylistMetadataFetchedConsumer,     PlaylistSubjects.PlaylistMetadataFetched);
        yield return DataBridgeConsumer(PlaylistMetadataFetchFailedConsumer, PlaylistSubjects.PlaylistMetadataFetchFailed);

        yield return WorkerConsumer(WorkerFetchPlaylistMetadataConsumer,     PlaylistSubjects.FetchPlaylistMetadataCommand);
    }

    private static ConsumerSpec DataBridgeConsumer(string durableName, string subject)
        => Consumer(durableName, subject, DataBridgeQueueGroup);

    private static ConsumerSpec WorkerConsumer(string durableName, string subject)
        => Consumer(durableName, subject, WorkerQueueGroup);

    private static ConsumerSpec Consumer(string durableName, string subject, string queueGroup)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(durableName),
            DeliverGroup = QueueGroup.From(queueGroup),
            FilterSubject = subject,
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(2),
            MaxDeliver = 10
        };
}
