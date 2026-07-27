namespace Shared.Messaging;

public static class BackgroundJobSubjects
{
    public const string ChannelScanRefreshRequest = "fs.channel.scan-refresh.request";
    public const string ChannelAssetRefreshRequest = "fs.channel.asset-refresh.request";
    public const string ChannelScanFullRequest = "fs.channel.scan-full.request";
    public const string DatabaseStaleMediaCleanupRequest = "fs.cleanup.database.stale-media.request";
    public const string ProcessedMessageCleanupRequest = "fs.cleanup.database.processed-messages.request";
    public const string DownloadHistoryCleanupRequest = "fs.cleanup.jobs.download-history.request";
    public const string DatabaseMaintenanceRequest = "fs.cleanup.database.maintenance.request";
    public const string DatabaseMaintenanceReindexRequest = "fs.cleanup.database.reindex.request";
    public const string SearchReindexRequest = "fs.index.search.rebuild.request";
    public const string AudioRenditionEncodeRequest = "fs.media.audio-rendition.encode.request";
    public const string StreamRenditionEncodeRequest = "fs.media.stream-rendition.encode.request";
    public const string BackupRequest = "fs.cleanup.backup.request";
}
