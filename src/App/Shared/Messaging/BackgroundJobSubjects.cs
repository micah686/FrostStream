namespace Shared.Messaging;

public static class BackgroundJobSubjects
{
    public const string ChannelUpdateCheckRequest = "fs.channel.update-check.request";
    public const string ChannelAssetRefreshRequest = "fs.channel.asset-refresh.request";
    public const string ChannelMediaListRequest = "fs.channel.media-list.request";
    public const string StaleDatabaseCleanupRequest = "fs.cleanup.database.stale.request";
    public const string DatabaseMaintenanceRequest = "fs.cleanup.database.maintenance.request";
    public const string SearchReindexRequest = "fs.index.search.rebuild.request";
    public const string FilesystemRescanRequest = "fs.media.filesystem.rescan.request";
    public const string HeavyDataProcessingRequest = "fs.media.processing.schedule.request";
}
