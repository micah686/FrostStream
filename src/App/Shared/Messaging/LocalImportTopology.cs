using Conduit.NATS;

namespace Shared.Messaging;

public sealed class LocalImportTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_IMPORT";
    public static readonly string[] SubjectFilters = ["import.cmd.>", "import.evt.>"];
    public const string ManifestObjectStoreBucket = "local-media-import-manifests";

    public const string DataBridgeQueueGroup = "databridge-imports";
    public const string WorkerQueueGroup = "workers-imports";

    public const string LocalImportFilePreparedConsumer = "databridge-local-import-file-prepared";
    public const string LocalImportFilePrepareFailedConsumer = "databridge-local-import-file-prepare-failed";
    public const string WorkerPrepareLocalImportFileConsumer = "worker-prepare-local-import-file";
    public const string WorkerScanLocalImportSourceConsumer = "worker-scan-local-import-source";
    public const string WorkerProbeImportSessionItemsConsumer = "worker-probe-import-session-items";
    public const string WorkerEnrichImportSessionItemConsumer = "worker-enrich-import-session-item";
    public const string ImportSessionItemsProbedConsumer = "databridge-import-session-items-probed";
    public const string ImportSessionItemsProbeFailedConsumer = "databridge-import-session-items-probe-failed";
    public const string ImportSessionItemEnrichedConsumer = "databridge-import-session-item-enriched";
    public const string ImportSessionItemEnrichFailedConsumer = "databridge-import-session-item-enrich-failed";

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
        yield return WorkerConsumer(WorkerScanLocalImportSourceConsumer, LocalImportSubjects.ScanLocalImportSourceCommand);
        yield return WorkerConsumer(WorkerProbeImportSessionItemsConsumer, LocalImportSubjects.ProbeImportSessionItemsCommand);
        yield return WorkerConsumer(WorkerEnrichImportSessionItemConsumer, LocalImportSubjects.EnrichImportSessionItemCommand);
        yield return DataBridgeConsumer(ImportSessionItemsProbedConsumer, LocalImportSubjects.ImportSessionItemsProbed);
        yield return DataBridgeConsumer(ImportSessionItemsProbeFailedConsumer, LocalImportSubjects.ImportSessionItemsProbeFailed);
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
