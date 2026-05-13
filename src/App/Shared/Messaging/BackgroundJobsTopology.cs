using FlySwattr.NATS.Abstractions;

namespace Shared.Messaging;

public sealed class BackgroundJobsTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_BACKGROUND";
    public const string DataBridgeQueueGroup = "databridge-bgjobs";
    public const string MediaProcessorQueueGroup = "mediaprocessor-bgjobs";
    public const string WorkerQueueGroup = "worker-bgjobs";

    public const string OrphanMetadataCleanupConsumer = "databridge-orphan-metadata-cleanup";

    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec
        {
            Name = StreamName.From(StreamNameValue),
            Subjects =
            [
                "fs.cleanup.>",
                "fs.db.>",
                "fs.index.>",
                "fs.media.>",
                "fs.channel.>"
            ],
            MaxAge = TimeSpan.FromDays(7),
            RetentionPolicy = StreamRetention.WorkQueue,
            StorageType = StorageType.File,
            Replicas = 1
        };
    }

    public IEnumerable<ConsumerSpec> GetConsumers()
    {
        yield return new ConsumerSpec
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(OrphanMetadataCleanupConsumer),
            DeliverGroup = QueueGroup.From(DataBridgeQueueGroup),
            FilterSubject = ScheduleSubjects.OrphanMetadataCleanupRequest,
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(5),
            MaxDeliver = 5
        };
    }
}
