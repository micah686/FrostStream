using Quartz;
using Scheduler.Jobs;

namespace Scheduler.Triggers;

internal static class TaskTypeRegistry
{
    public const string ChannelScanRefresh = "channel_scan_refresh";
    public const string ChannelAssetRefresh = "channel-asset-refresh";
    public const string ChannelScanFull = "channel_scan_full";
    public const string DatabaseStaleMediaCleanup = "db-stale-media-cleanup";
    public const string DatabaseMaintenance = "db-maintenance";
    public const string DatabaseMaintenanceReindex = "database_maintenance_reindex";
    public const string SearchReindex = "search-reindex";
    public const string DownloadHistoryCleanup = "download-history-cleanup";
    public const string ImportSessionCleanup = "import_session_cleanup";
    public const string Backup = "backup";

    private static readonly IReadOnlyDictionary<string, Type> JobTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        [ChannelScanRefresh] = typeof(ChannelScanRefreshJob),
        [ChannelAssetRefresh] = typeof(ChannelAssetRefreshJob),
        [ChannelScanFull] = typeof(ChannelScanFullJob),
        [DatabaseStaleMediaCleanup] = typeof(DatabaseStaleMediaCleanupJob),
        [DatabaseMaintenance] = typeof(DatabaseMaintenanceJob),
        [DatabaseMaintenanceReindex] = typeof(DatabaseMaintenanceReindexJob),
        [SearchReindex] = typeof(SearchReindexJob),
        [DownloadHistoryCleanup] = typeof(DownloadHistoryCleanupJob),
        [ImportSessionCleanup] = typeof(ImportSessionCleanupJob),
        [Backup] = typeof(BackupJob)
    };

    public static bool TryGetJobType(string taskType, out Type jobType)
        => JobTypes.TryGetValue(taskType, out jobType!);

    public static Type GetJobType(string taskType)
        => TryGetJobType(taskType, out var jobType)
            ? jobType
            : throw new SchedulerException($"No Quartz job is registered for task_type '{taskType}'.");
}
