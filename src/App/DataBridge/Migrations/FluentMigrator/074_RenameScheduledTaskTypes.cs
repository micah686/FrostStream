using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(74, "Rename channel_update_check/channel_media_list/stale_database_cleanup task types")]
public sealed class M074_RenameScheduledTaskTypes : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            UPDATE scheduling.scheduled_tasks SET task_type = 'channel_scan_refresh', last_updated = now() WHERE task_type = 'channel_update_check';
            UPDATE scheduling.scheduled_tasks SET task_type = 'channel_scan_full', last_updated = now() WHERE task_type = 'channel_media_list';
            UPDATE scheduling.scheduled_tasks SET task_type = 'database_stale_media_cleanup', last_updated = now() WHERE task_type = 'stale_database_cleanup';
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            UPDATE scheduling.scheduled_tasks SET task_type = 'channel_update_check', last_updated = now() WHERE task_type = 'channel_scan_refresh';
            UPDATE scheduling.scheduled_tasks SET task_type = 'channel_media_list', last_updated = now() WHERE task_type = 'channel_scan_full';
            UPDATE scheduling.scheduled_tasks SET task_type = 'stale_database_cleanup', last_updated = now() WHERE task_type = 'database_stale_media_cleanup';
            """);
    }
}
