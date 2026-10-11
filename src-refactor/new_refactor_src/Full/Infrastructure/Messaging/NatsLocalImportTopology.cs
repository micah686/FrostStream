using Conduit.NATS;
using Shared.Messaging;
using static Shared.Messaging.LocalImportTopology;

namespace FrostStream.Full.Infrastructure;

public sealed class NatsLocalImportTopology : ITopologySource
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
        yield return DataBridgeConsumer(LocalImportFilePreparedConsumer, LocalImportSubjects.LocalImportFilePrepared);
        yield return DataBridgeConsumer(LocalImportFilePrepareFailedConsumer, LocalImportSubjects.LocalImportFilePrepareFailed);
        yield return WorkerConsumer(WorkerPrepareLocalImportFileConsumer, LocalImportSubjects.PrepareLocalImportFileCommand);
        yield return WorkerConsumer(WorkerDeleteLocalImportSourceConsumer, LocalImportSubjects.DeleteLocalImportSourceCommand);
        yield return WorkerConsumer(WorkerScanLocalImportSourceConsumer, LocalImportSubjects.ScanLocalImportSourceCommand);
        yield return WorkerConsumer(WorkerProbeImportSessionItemsConsumer, LocalImportSubjects.ProbeImportSessionItemsCommand);
        yield return WorkerConsumer(WorkerEnrichImportSessionItemConsumer, LocalImportSubjects.EnrichImportSessionItemCommand);
        yield return DataBridgeConsumer(ImportSessionItemsProbedConsumer, LocalImportSubjects.ImportSessionItemsProbed);
        yield return DataBridgeConsumer(ImportSessionItemEnrichedConsumer, LocalImportSubjects.ImportSessionItemEnriched);
        yield return DataBridgeConsumer(ImportSessionItemEnrichFailedConsumer, LocalImportSubjects.ImportSessionItemEnrichFailed);
    }

    public IEnumerable<ObjectStoreSpec> GetObjectStores()
    {
        yield return new ObjectStoreSpec
        {
            Name = BucketName.From(ManifestObjectStoreBucket),
            StorageType = StorageType.File,
            MaxAge = TimeSpan.FromDays(7),
            Replicas = 1,
            Description = "Temporary local media import manifests"
        };
    }

    public static ConsumerSpec TaggedWorkerConsumerSpec(string baseConsumerName, string baseSubject, string tag)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From($"{baseConsumerName}-{tag}"),
            DeliverGroup = QueueGroup.From($"workers-imports-{tag}"),
            FilterSubject = LocalImportSubjects.Tagged(baseSubject, tag),
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(2),
            MaxDeliver = 10
        };

    private static ConsumerSpec DataBridgeConsumer(string durableName, string subject)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(durableName),
            DeliverGroup = QueueGroup.From(DataBridgeQueueGroup),
            FilterSubject = subject,
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(2),
            MaxDeliver = 10
        };

    private static ConsumerSpec WorkerConsumer(string durableName, string subject)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(durableName),
            DeliverGroup = QueueGroup.From(WorkerQueueGroup),
            FilterSubject = subject,
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(2),
            MaxDeliver = 10
        };
}
