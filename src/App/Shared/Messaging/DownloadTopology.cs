using Conduit.NATS;

namespace Shared.Messaging;

/// <summary>
/// JetStream topology for the download flow. One stream (<c>FROSTSTREAM_DOWNLOAD</c>) covers
/// every <c>download.&gt;</c> subject; durable consumers exist per ingress path so messages
/// survive service restarts and replay correctly.
///
/// Lives in <c>Shared</c> so DataBridge and Worker can both register it via
/// <c>AddNatsTopologySource&lt;DownloadTopology&gt;()</c>. Provisioning is idempotent —
/// whichever service starts first creates the stream and the consumers, and the other
/// service's Ensure* calls become no-ops.
/// </summary>
public sealed class DownloadTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_DOWNLOAD";
    public const string SubjectFilter = "download.>";

    public const string DataBridgeQueueGroup = "databridge-downloads";
    public const string WorkerQueueGroup = "workers";

    // DataBridge consumer durable names.
    public const string DownloadRequestedConsumer        = "databridge-download-requested";
    public const string MetadataFetchedConsumer          = "databridge-metadata-fetched";
    public const string MetadataFetchFailedConsumer      = "databridge-metadata-fetch-failed";
    public const string DownloadCompletedConsumer        = "databridge-download-completed";
    public const string DownloadFailedConsumer           = "databridge-download-failed";
    public const string UploadCompletedConsumer          = "databridge-upload-completed";
    public const string UploadFailedConsumer             = "databridge-upload-failed";
    public const string TempFileDeletedConsumer          = "databridge-temp-file-deleted";
    public const string TempFileDeleteFailedConsumer     = "databridge-temp-file-delete-failed";
    public const string UploadedObjectDeletedConsumer    = "databridge-uploaded-object-deleted";
    public const string UploadedObjectDeleteFailedConsumer = "databridge-uploaded-object-delete-failed";
    public const string LocalImportUploadCompletedConsumer = "databridge-local-import-upload-completed";
    public const string LocalImportUploadFailedConsumer = "databridge-local-import-upload-failed";
    public const string LocalImportUploadedObjectDeletedConsumer = "databridge-local-import-uploaded-object-deleted";
    public const string LocalImportUploadedObjectDeleteFailedConsumer = "databridge-local-import-uploaded-object-delete-failed";

    // Worker consumer durable names.
    public const string WorkerFetchMetadataConsumer        = "worker-fetch-metadata";
    public const string WorkerDownloadVideoConsumer        = "worker-download-video";
    public const string WorkerUploadObjectConsumer         = "worker-upload-object";
    public const string WorkerDeleteTempFileConsumer       = "worker-delete-temp-file";
    public const string WorkerDeleteUploadedObjectConsumer = "worker-delete-uploaded-object";

    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec
        {
            Name = StreamName.From(StreamNameValue),
            Subjects = [SubjectFilter],
            MaxAge = TimeSpan.FromDays(30),
            RetentionPolicy = StreamRetention.Limits,
            StorageType = StorageType.File,
            Replicas = 1
        };
    }

    public IEnumerable<ConsumerSpec> GetConsumers()
    {
        // DataBridge ingress: DownloadRequested + every Worker-emitted result event.
        yield return DataBridgeConsumer(DownloadRequestedConsumer,         DownloadSubjects.DownloadRequested);
        yield return DataBridgeConsumer(MetadataFetchedConsumer,           DownloadSubjects.MetadataFetched);
        yield return DataBridgeConsumer(MetadataFetchFailedConsumer,       DownloadSubjects.MetadataFetchFailed);
        yield return DataBridgeConsumer(DownloadCompletedConsumer,         DownloadSubjects.DownloadCompleted);
        yield return DataBridgeConsumer(DownloadFailedConsumer,            DownloadSubjects.DownloadFailed);
        yield return DataBridgeConsumer(UploadCompletedConsumer,           DownloadSubjects.UploadCompleted);
        yield return DataBridgeConsumer(UploadFailedConsumer,              DownloadSubjects.UploadFailed);
        yield return DataBridgeConsumer(TempFileDeletedConsumer,           DownloadSubjects.TempFileDeleted);
        yield return DataBridgeConsumer(TempFileDeleteFailedConsumer,      DownloadSubjects.TempFileDeleteFailed);
        yield return DataBridgeConsumer(UploadedObjectDeletedConsumer,     DownloadSubjects.UploadedObjectDeleted);
        yield return DataBridgeConsumer(UploadedObjectDeleteFailedConsumer, DownloadSubjects.UploadedObjectDeleteFailed);
        yield return DataBridgeConsumer(LocalImportUploadCompletedConsumer, DownloadSubjects.UploadCompleted);
        yield return DataBridgeConsumer(LocalImportUploadFailedConsumer, DownloadSubjects.UploadFailed);
        yield return DataBridgeConsumer(LocalImportUploadedObjectDeletedConsumer, DownloadSubjects.UploadedObjectDeleted);
        yield return DataBridgeConsumer(LocalImportUploadedObjectDeleteFailedConsumer, DownloadSubjects.UploadedObjectDeleteFailed);

        // Worker-side command consumers — durable so a Worker pod restart resumes mid-flight commands.
        yield return WorkerConsumer(WorkerFetchMetadataConsumer,        DownloadSubjects.FetchMetadataCommand);
        yield return WorkerConsumer(WorkerDownloadVideoConsumer,        DownloadSubjects.DownloadVideoCommand);
        yield return WorkerConsumer(WorkerUploadObjectConsumer,         DownloadSubjects.UploadObjectCommand);
        yield return WorkerConsumer(WorkerDeleteTempFileConsumer,       DownloadSubjects.DeleteTempFileCommand);
        yield return WorkerConsumer(WorkerDeleteUploadedObjectConsumer, DownloadSubjects.DeleteUploadedObjectCommand);
    }

    /// <summary>
    /// Builds a durable consumer spec for a tagged worker command. Workers call
    /// <see cref="ITopologyManager.EnsureConsumerAsync"/> with this spec at startup for each
    /// of their configured tags. Multiple workers sharing the same tag use the same durable
    /// name, so they form a queue group and share load.
    /// </summary>
    public static ConsumerSpec TaggedWorkerConsumerSpec(string baseConsumerName, string baseSubject, string tag)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From($"{baseConsumerName}-{tag}"),
            DeliverGroup = QueueGroup.From($"workers-{tag}"),
            FilterSubject = DownloadSubjects.Tagged(baseSubject, tag),
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(2),
            MaxDeliver = 10
        };

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
