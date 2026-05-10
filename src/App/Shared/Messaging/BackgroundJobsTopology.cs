using FlySwattr.NATS.Abstractions;

namespace Shared.Messaging;

/// <summary>
/// JetStream topology for trigger commands published by the Scheduler service.
///
/// Lives in <c>Shared</c> so Scheduler (publisher) and DataBridge / MediaProcessor /
/// Worker (consumers) can each register it via
/// <c>AddNatsTopologySource&lt;BackgroundJobsTopology&gt;()</c>. Provisioning is
/// idempotent — whichever service starts first creates the stream and consumers,
/// and the others' calls are no-ops.
///
/// Stream uses <see cref="StreamRetention.WorkQueue"/> retention: once a consumer
/// acks a message, it's gone. Combined with the deterministic idempotency key on
/// each command (set as <c>Nats-Msg-Id</c>) this gives "exactly-once-effectful"
/// semantics for catch-up runs even when Scheduler and Worker both restart.
///
/// Subject filter is broad enough to host every future scheduled-job kind without
/// requiring a new stream:
///   <c>fs.cleanup.&gt;</c> — cleanups (orphan metadata, db housekeeping, ...)
///   <c>fs.index.&gt;</c>   — full / partial reindex requests
///   <c>fs.media.&gt;</c>   — thumbnail / artwork / transcoding (MediaProcessor)
///   <c>fs.channel.&gt;</c> — channel scan / expand requests
/// </summary>
public sealed class BackgroundJobsTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_BACKGROUND";

    public const string DataBridgeQueueGroup     = "databridge-bgjobs";
    public const string MediaProcessorQueueGroup = "mediaprocessor-bgjobs";
    public const string WorkerQueueGroup         = "worker-bgjobs";

    // Consumer durable names.
    public const string OrphanMetadataCleanupConsumer = "databridge-orphan-metadata-cleanup";

    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec
        {
            Name = StreamName.From(StreamNameValue),
            Subjects =
            [
                "fs.cleanup.>",
                "fs.index.>",
                "fs.media.>",
                "fs.channel.>"
            ],
            // 7 days is enough headroom for catch-up after extended outages without
            // hoarding old commands forever.
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
            StreamName    = StreamName.From(StreamNameValue),
            DurableName   = ConsumerName.From(OrphanMetadataCleanupConsumer),
            DeliverGroup  = QueueGroup.From(DataBridgeQueueGroup),
            FilterSubject = ScheduleSubjects.OrphanMetadataCleanupRequest,
            AckPolicy     = AckPolicy.Explicit,
            // Cleanup is a single-shot SQL DELETE; 5 minutes covers slow nights.
            AckWait       = TimeSpan.FromMinutes(5),
            MaxDeliver    = 5
        };
    }
}
