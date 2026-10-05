using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(86, "Rename scheduled task keys to canonical task-oriented names")]
public sealed class M086_RenameScheduledTaskKeys : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            WITH key_map(old_key, new_key) AS (
                VALUES
                    ('channel-update-check', 'channel-scan-refresh'),
                    ('daily-channel-full-rescan', 'channel-full-rescan'),
                    ('monthly-db-maintenance', 'db-maintenance'),
                    ('monthly-stale-media-cleanup', 'db-stale-media-cleanup'),
                    ('nightly-download-history-cleanup', 'download-history-cleanup'),
                    ('weekly-channel-asset-refresh', 'channel-asset-refresh'),
                    ('nightly-import-session-cleanup', 'import-session-cleanup'),
                    ('weekly-search-reindex', 'search-reindex')
            )
            DELETE FROM scheduling.scheduled_tasks old_task
            USING key_map
            WHERE old_task."key" = key_map.old_key
              AND EXISTS (
                  SELECT 1
                  FROM scheduling.scheduled_tasks existing
                  WHERE existing."key" = key_map.new_key
              );

            WITH key_map(old_key, new_key) AS (
                VALUES
                    ('channel-update-check', 'channel-scan-refresh'),
                    ('daily-channel-full-rescan', 'channel-full-rescan'),
                    ('monthly-db-maintenance', 'db-maintenance'),
                    ('monthly-stale-media-cleanup', 'db-stale-media-cleanup'),
                    ('nightly-download-history-cleanup', 'download-history-cleanup'),
                    ('weekly-channel-asset-refresh', 'channel-asset-refresh'),
                    ('nightly-import-session-cleanup', 'import-session-cleanup'),
                    ('weekly-search-reindex', 'search-reindex')
            )
            UPDATE scheduling.scheduled_tasks task
            SET "key" = key_map.new_key,
                last_updated = now()
            FROM key_map
            WHERE task."key" = key_map.old_key;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            WITH key_map(old_key, new_key) AS (
                VALUES
                    ('channel-update-check', 'channel-scan-refresh'),
                    ('daily-channel-full-rescan', 'channel-full-rescan'),
                    ('monthly-db-maintenance', 'db-maintenance'),
                    ('monthly-stale-media-cleanup', 'db-stale-media-cleanup'),
                    ('nightly-download-history-cleanup', 'download-history-cleanup'),
                    ('weekly-channel-asset-refresh', 'channel-asset-refresh'),
                    ('nightly-import-session-cleanup', 'import-session-cleanup'),
                    ('weekly-search-reindex', 'search-reindex')
            )
            DELETE FROM scheduling.scheduled_tasks new_task
            USING key_map
            WHERE new_task."key" = key_map.new_key
              AND EXISTS (
                  SELECT 1
                  FROM scheduling.scheduled_tasks existing
                  WHERE existing."key" = key_map.old_key
              );

            WITH key_map(old_key, new_key) AS (
                VALUES
                    ('channel-update-check', 'channel-scan-refresh'),
                    ('daily-channel-full-rescan', 'channel-full-rescan'),
                    ('monthly-db-maintenance', 'db-maintenance'),
                    ('monthly-stale-media-cleanup', 'db-stale-media-cleanup'),
                    ('nightly-download-history-cleanup', 'download-history-cleanup'),
                    ('weekly-channel-asset-refresh', 'channel-asset-refresh'),
                    ('nightly-import-session-cleanup', 'import-session-cleanup'),
                    ('weekly-search-reindex', 'search-reindex')
            )
            UPDATE scheduling.scheduled_tasks task
            SET "key" = key_map.old_key,
                last_updated = now()
            FROM key_map
            WHERE task."key" = key_map.new_key;
            """);
    }
}
