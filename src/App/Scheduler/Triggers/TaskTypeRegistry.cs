using Quartz;
using Scheduler.Jobs;

namespace Scheduler.Triggers;

internal static class TaskTypeRegistry
{
    public const string OrphanMetadataCleanup = "orphan_metadata_cleanup";
    public const string ChannelUpdateCheck = "channel_update_check";
    public const string ChannelAssetRefresh = "channel_asset_refresh";
    public const string ChannelMediaList = "channel_media_list";
    public const string StaleDatabaseCleanup = "stale_database_cleanup";
    public const string WatchedItemAutoDelete = "watched_item_auto_delete";
    public const string DatabaseMaintenance = "database_maintenance";
    public const string SearchReindex = "search_reindex";
    public const string FilesystemRescan = "filesystem_rescan";
    public const string ProcessedMessageCleanup = "processed_message_cleanup";
    public const string Backup = "backup";

    private static readonly IReadOnlyDictionary<string, Type> JobTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        [OrphanMetadataCleanup] = typeof(OrphanMetadataCleanupTriggerJob),
        [ChannelUpdateCheck] = typeof(ChannelUpdateCheckJob),
        [ChannelAssetRefresh] = typeof(ChannelAssetRefreshJob),
        [ChannelMediaList] = typeof(ChannelMediaListJob),
        [StaleDatabaseCleanup] = typeof(StaleDatabaseCleanupJob),
        [WatchedItemAutoDelete] = typeof(WatchedItemAutoDeleteJob),
        [DatabaseMaintenance] = typeof(DatabaseMaintenanceJob),
        [SearchReindex] = typeof(SearchReindexJob),
        [FilesystemRescan] = typeof(FilesystemRescanJob),
        [ProcessedMessageCleanup] = typeof(ProcessedMessageCleanupJob),
        [Backup] = typeof(BackupJob)
    };

    public static bool TryGetJobType(string taskType, out Type jobType)
        => JobTypes.TryGetValue(taskType, out jobType!);

    public static Type GetJobType(string taskType)
        => TryGetJobType(taskType, out var jobType)
            ? jobType
            : throw new SchedulerException($"No Quartz job is registered for task_type '{taskType}'.");
}
