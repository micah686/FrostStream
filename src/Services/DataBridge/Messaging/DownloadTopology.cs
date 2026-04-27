using FlySwattr.NATS.Abstractions;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// JetStream topology for the download flow. One stream (<c>FROSTSTREAM_DOWNLOAD</c>) covers
/// every <c>download.&gt;</c> subject; durable consumers exist per ingress path so messages
/// survive service restarts and replay correctly.
///
/// The current ingress services (<see cref="DownloadRequestedIngressService"/> and
/// <see cref="DownloadEventsConsumerService"/>) and the Worker stubs still use core NATS
/// (<see cref="IMessageBus"/>) for delivery — this topology is forward-compatible: when those
/// services migrate to <see cref="IJetStreamConsumer"/>.<c>ConsumePullAsync</c>, they bind to
/// these existing durables by name without further provisioning.
/// </summary>
public sealed class DownloadTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_DOWNLOAD";
    public const string SubjectFilter = "download.>";

    private const string DataBridgeQueueGroup = "databridge-downloads";
    private const string WorkerQueueGroup = "workers";

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
        yield return DataBridgeConsumer("databridge-download-requested", DownloadSubjects.DownloadRequested);

        yield return DataBridgeConsumer("databridge-metadata-fetched", DownloadSubjects.MetadataFetched);
        yield return DataBridgeConsumer("databridge-metadata-fetch-failed", DownloadSubjects.MetadataFetchFailed);
        yield return DataBridgeConsumer("databridge-download-completed", DownloadSubjects.DownloadCompleted);
        yield return DataBridgeConsumer("databridge-download-failed", DownloadSubjects.DownloadFailed);
        yield return DataBridgeConsumer("databridge-upload-completed", DownloadSubjects.UploadCompleted);
        yield return DataBridgeConsumer("databridge-upload-failed", DownloadSubjects.UploadFailed);
        yield return DataBridgeConsumer("databridge-temp-file-deleted", DownloadSubjects.TempFileDeleted);
        yield return DataBridgeConsumer("databridge-temp-file-delete-failed", DownloadSubjects.TempFileDeleteFailed);
        yield return DataBridgeConsumer("databridge-uploaded-object-deleted", DownloadSubjects.UploadedObjectDeleted);
        yield return DataBridgeConsumer("databridge-uploaded-object-delete-failed", DownloadSubjects.UploadedObjectDeleteFailed);

        // Worker-side command consumers — durable so a Worker pod restart resumes mid-flight commands.
        yield return WorkerConsumer("worker-fetch-metadata", DownloadSubjects.FetchMetadataCommand);
        yield return WorkerConsumer("worker-download-video", DownloadSubjects.DownloadVideoCommand);
        yield return WorkerConsumer("worker-upload-object", DownloadSubjects.UploadObjectCommand);
        yield return WorkerConsumer("worker-delete-temp-file", DownloadSubjects.DeleteTempFileCommand);
        yield return WorkerConsumer("worker-delete-uploaded-object", DownloadSubjects.DeleteUploadedObjectCommand);
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
