using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(84, "Align scheduled task types with schedule key mappings")]
public sealed class M084_RenameRequestedScheduledTaskTypes : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            UPDATE scheduling.scheduled_tasks
            SET task_type = CASE task_type
                WHEN 'database_maintenance' THEN 'db-maintenance'
                WHEN 'stale_database_cleanup' THEN 'db-stale-media-cleanup'
                WHEN 'database_stale_media_cleanup' THEN 'db-stale-media-cleanup'
                WHEN 'channel_asset_refresh' THEN 'channel-asset-refresh'
                WHEN 'search_reindex' THEN 'search-reindex'
                WHEN 'download_history_cleanup' THEN 'download-history-cleanup'
                ELSE task_type
            END,
            last_updated = now()
            WHERE task_type IN ('database_maintenance', 'stale_database_cleanup',
                'database_stale_media_cleanup', 'channel_asset_refresh',
                'search_reindex', 'download_history_cleanup');
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            UPDATE scheduling.scheduled_tasks
            SET task_type = CASE task_type
                WHEN 'db-maintenance' THEN 'database_maintenance'
                WHEN 'db-stale-media-cleanup' THEN 'database_stale_media_cleanup'
                WHEN 'channel-asset-refresh' THEN 'channel_asset_refresh'
                WHEN 'search-reindex' THEN 'search_reindex'
                WHEN 'download-history-cleanup' THEN 'download_history_cleanup'
                ELSE task_type
            END,
            last_updated = now()
            WHERE task_type IN ('db-maintenance', 'db-stale-media-cleanup',
                'channel-asset-refresh', 'search-reindex', 'download-history-cleanup');
            """);
    }
}
