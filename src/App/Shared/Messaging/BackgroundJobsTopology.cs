using Conduit.NATS;

namespace Shared.Messaging;

public sealed class BackgroundJobsTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_BACKGROUND";
    public const string DataBridgeQueueGroup = "databridge-bgjobs";
    public const string MediaProcessorQueueGroup = "mediaprocessor-bgjobs";
    public const string WorkerQueueGroup = "worker-bgjobs";

    public const string ProcessedMessageCleanupConsumer = "databridge-processed-message-cleanup";
    public const string SearchReindexConsumer = "databridge-search-reindex";
    public const string DatabaseMaintenanceConsumer = "databridge-database-maintenance";
    public const string DatabaseMaintenanceReindexConsumer = "databridge-database-maintenance-reindex";
    public const string DatabaseStaleMediaCleanupConsumer = "databridge-database-stale-media-cleanup";
    public const string WorkerChannelScanRefreshConsumer = "worker-channel-scan-refresh";
    public const string WorkerChannelScanFullConsumer = "worker-channel-scan-full";
    public const string WorkerChannelAssetRefreshConsumer = "worker-channel-asset-refresh";
    public const string MediaProcessorAudioRenditionConsumer = "mediaprocessor-audio-rendition";
    public const string MediaProcessorStreamRenditionConsumer = "mediaprocessor-stream-rendition";
    // The durable value is intentionally retained from the former DataBridge owner so queued
    // schedule messages survive upgrades; BackupService is now the only process that consumes it.
    public const string BackupServiceBackupConsumer = "databridge-backup";

    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec
        {
            Name = StreamName.From(StreamNameValue),
            // Only the JetStream background-job subjects may be captured here. Broad wildcards
            // (e.g. "fs.cleanup.>") must not be used: they also capture core request/reply
            // subjects (worker file ops, filesystem reconcile), and JetStream's publish-ack
            // then races the responder's reply on the request inbox.
            Subjects =
            [
                BackgroundJobSubjects.ChannelScanRefreshRequest,
                BackgroundJobSubjects.ChannelAssetRefreshRequest,
                BackgroundJobSubjects.ChannelScanFullRequest,
                BackgroundJobSubjects.DatabaseStaleMediaCleanupRequest,
                BackgroundJobSubjects.ProcessedMessageCleanupRequest,
                BackgroundJobSubjects.DatabaseMaintenanceRequest,
                BackgroundJobSubjects.DatabaseMaintenanceReindexRequest,
                BackgroundJobSubjects.SearchReindexRequest,
                BackgroundJobSubjects.AudioRenditionEncodeRequest,
                BackgroundJobSubjects.StreamRenditionEncodeRequest,
                BackgroundJobSubjects.BackupRequest
            ],
            MaxAge = TimeSpan.FromDays(7),
            RetentionPolicy = StreamRetention.WorkQueue,
            StorageType = StorageType.File,
            Replicas = 1
        };
    }

    public IEnumerable<ConsumerSpec> GetConsumers()
    {
        yield return DataBridgeConsumer(ProcessedMessageCleanupConsumer, BackgroundJobSubjects.ProcessedMessageCleanupRequest, TimeSpan.FromMinutes(15), maxDeliver: 5);
        yield return DataBridgeConsumer(SearchReindexConsumer, BackgroundJobSubjects.SearchReindexRequest, TimeSpan.FromMinutes(30), maxDeliver: 3);
        yield return DataBridgeConsumer(DatabaseMaintenanceConsumer, BackgroundJobSubjects.DatabaseMaintenanceRequest, TimeSpan.FromHours(2), maxDeliver: 3);
        yield return DataBridgeConsumer(DatabaseMaintenanceReindexConsumer, BackgroundJobSubjects.DatabaseMaintenanceReindexRequest, TimeSpan.FromHours(24), maxDeliver: 2);
        yield return DataBridgeConsumer(DatabaseStaleMediaCleanupConsumer, BackgroundJobSubjects.DatabaseStaleMediaCleanupRequest, TimeSpan.FromMinutes(15), maxDeliver: 5);
        yield return WorkerConsumer(WorkerChannelScanRefreshConsumer, BackgroundJobSubjects.ChannelScanRefreshRequest, TimeSpan.FromMinutes(30), maxDeliver: 5);
        yield return WorkerConsumer(WorkerChannelScanFullConsumer, BackgroundJobSubjects.ChannelScanFullRequest, TimeSpan.FromHours(2), maxDeliver: 3);
        yield return WorkerConsumer(WorkerChannelAssetRefreshConsumer, BackgroundJobSubjects.ChannelAssetRefreshRequest, TimeSpan.FromMinutes(30), maxDeliver: 3);
        // Encodes run arbitrarily long, so MediaProcessor extends the short ack window with
        // in-progress acks every 30s while ffmpeg works; a dead encoder is redelivered quickly.
        yield return MediaProcessorConsumer(MediaProcessorAudioRenditionConsumer, BackgroundJobSubjects.AudioRenditionEncodeRequest, TimeSpan.FromMinutes(2), maxDeliver: 3);
        yield return MediaProcessorConsumer(MediaProcessorStreamRenditionConsumer, BackgroundJobSubjects.StreamRenditionEncodeRequest, TimeSpan.FromMinutes(2), maxDeliver: 2);
        yield return DataBridgeConsumer(BackupServiceBackupConsumer, BackgroundJobSubjects.BackupRequest, TimeSpan.FromHours(2), maxDeliver: 2);
    }

    public IEnumerable<ObjectStoreSpec> GetObjectStores()
        => Array.Empty<ObjectStoreSpec>();

    private static ConsumerSpec DataBridgeConsumer(string durableName, string subject, TimeSpan ackWait, int maxDeliver)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(durableName),
            DeliverGroup = QueueGroup.From(DataBridgeQueueGroup),
            FilterSubject = subject,
            AckPolicy = AckPolicy.Explicit,
            AckWait = ackWait,
            MaxDeliver = maxDeliver
        };

    private static ConsumerSpec WorkerConsumer(string durableName, string subject, TimeSpan ackWait, int maxDeliver)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(durableName),
            DeliverGroup = QueueGroup.From(WorkerQueueGroup),
            FilterSubject = subject,
            AckPolicy = AckPolicy.Explicit,
            AckWait = ackWait,
            MaxDeliver = maxDeliver
        };

    private static ConsumerSpec MediaProcessorConsumer(string durableName, string subject, TimeSpan ackWait, int maxDeliver)
        => new()
        {
            StreamName = StreamName.From(StreamNameValue),
            DurableName = ConsumerName.From(durableName),
            DeliverGroup = QueueGroup.From(MediaProcessorQueueGroup),
            FilterSubject = subject,
            AckPolicy = AckPolicy.Explicit,
            AckWait = ackWait,
            MaxDeliver = maxDeliver
        };
}
