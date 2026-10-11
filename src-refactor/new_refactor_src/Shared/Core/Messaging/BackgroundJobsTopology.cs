
namespace Shared.Messaging;

public static class BackgroundJobsTopology
{
    public const string StreamNameValue = "FROSTSTREAM_BACKGROUND";
    public const string DataBridgeQueueGroup = "databridge-bgjobs";
    public const string MediaProcessorQueueGroup = "mediaprocessor-bgjobs";
    public const string WorkerQueueGroup = "worker-bgjobs";

    public const string DownloadHistoryCleanupConsumer = "databridge-download-history-cleanup";
    public const string ImportSessionCleanupConsumer = "databridge-import-session-cleanup";
    public const string SearchReindexConsumer = "databridge-search-reindex";
    public const string DatabaseMaintenanceConsumer = "databridge-database-maintenance";
    public const string DatabaseMaintenanceReindexConsumer = "databridge-database-maintenance-reindex";
    public const string DatabaseStaleMediaCleanupConsumer = "databridge-database-stale-media-cleanup";
    public const string WorkerChannelScanRefreshConsumer = "worker-channel-scan-refresh";
    public const string WorkerChannelScanFullConsumer = "worker-channel-scan-full";
    public const string WorkerChannelAssetRefreshConsumer = "worker-channel-asset-refresh";
    public const string MediaProcessorAudioRenditionConsumer = "mediaprocessor-audio-rendition";
    public const string LiveChatIngestConsumer = "databridge-livechat-ingest";
    public const string LiveChatBackfillConsumer = "databridge-livechat-backfill";
    public const string MediaProcessorStreamRenditionConsumer = "mediaprocessor-stream-rendition";
    public const string MediaProcessorThumbnailGenerationConsumer = "mediaprocessor-thumbnail-generation";

}
