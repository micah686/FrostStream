using Conduit.NATS;

namespace Shared.Messaging;

/// <summary>Fresh Download Flow V2 topology. Artifact transfer has its own stream.</summary>
public sealed class DownloadTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_DOWNLOAD_V2";
    public const string SubjectFilter = "download.v2.>";

    public const string GroupRequestedConsumer = "databridge-download-v2-group-requested";
    public const string DownloadRequestedConsumer = "databridge-download-v2-job-requested";
    public const string MetadataFetchedConsumer = "databridge-download-v2-metadata-succeeded";
    public const string MetadataFetchFailedConsumer = "databridge-download-v2-metadata-failed";
    public const string DownloadCompletedConsumer = "databridge-download-v2-media-succeeded";
    public const string DownloadFailedConsumer = "databridge-download-v2-media-failed";
    public const string StageStartedConsumer = "databridge-download-v2-stage-started";
    public const string StageHeartbeatConsumer = "databridge-download-v2-stage-heartbeat";
    public const string StageSucceededConsumer = "databridge-download-v2-stage-succeeded";
    public const string StageFailedConsumer = "databridge-download-v2-stage-failed";
    public const string StageStoppedConsumer = "databridge-download-v2-stage-stopped";
    public const string GroupExpansionSucceededConsumer = "databridge-download-v2-group-expansion-succeeded";
    public const string GroupExpansionFailedConsumer = "databridge-download-v2-group-expansion-failed";

    public const string WorkerFetchMetadataConsumer = "worker-download-v2-metadata";
    public const string WorkerDownloadVideoConsumer = "worker-download-v2-media";

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
        yield return DataBridge(GroupRequestedConsumer, DownloadSubjects.GroupRequested);
        yield return DataBridge(DownloadRequestedConsumer, DownloadSubjects.DownloadRequested);
        yield return DataBridge(MetadataFetchedConsumer, DownloadSubjects.MetadataFetched);
        yield return DataBridge(MetadataFetchFailedConsumer, DownloadSubjects.MetadataFetchFailed);
        yield return DataBridge(DownloadCompletedConsumer, DownloadSubjects.DownloadCompleted);
        yield return DataBridge(DownloadFailedConsumer, DownloadSubjects.DownloadFailed);
        yield return DataBridge(StageStartedConsumer, DownloadSubjects.StageStarted);
        yield return DataBridge(StageHeartbeatConsumer, DownloadSubjects.StageHeartbeat);
        yield return DataBridge(StageSucceededConsumer, DownloadSubjects.StageSucceeded);
        yield return DataBridge(StageFailedConsumer, DownloadSubjects.StageFailed);
        yield return DataBridge(StageStoppedConsumer, DownloadSubjects.StageStopped);
        yield return DataBridge(GroupExpansionSucceededConsumer, DownloadSubjects.GroupExpansionSucceeded);
        yield return DataBridge(GroupExpansionFailedConsumer, DownloadSubjects.GroupExpansionFailed);
        yield return Worker(WorkerFetchMetadataConsumer, DownloadSubjects.FetchMetadataCommand);
        yield return Worker(WorkerDownloadVideoConsumer, DownloadSubjects.DownloadVideoCommand);
    }

    public static ConsumerSpec TaggedWorkerConsumerSpec(string baseConsumerName, string baseSubject, string tag) => new()
    {
        StreamName = StreamName.From(StreamNameValue),
        DurableName = ConsumerName.From($"{baseConsumerName}-{tag}"),
        DeliverGroup = QueueGroup.From($"download-v2-workers-{tag}"),
        FilterSubject = DownloadSubjects.Tagged(baseSubject, tag),
        AckPolicy = AckPolicy.Explicit,
        AckWait = TimeSpan.FromMinutes(2),
        MaxDeliver = 10
    };

    private static ConsumerSpec DataBridge(string durableName, string subject) => Consumer(durableName, subject, "databridge-download-v2");
    private static ConsumerSpec Worker(string durableName, string subject) => Consumer(durableName, subject, "download-v2-workers");

    private static ConsumerSpec Consumer(string durableName, string subject, string group) => new()
    {
        StreamName = StreamName.From(StreamNameValue),
        DurableName = ConsumerName.From(durableName),
        DeliverGroup = QueueGroup.From(group),
        FilterSubject = subject,
        AckPolicy = AckPolicy.Explicit,
        AckWait = TimeSpan.FromMinutes(2),
        MaxDeliver = 10
    };
}
