using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(79, "Seed nightly download_history_cleanup scheduler task")]
public sealed class M079_SeedDownloadHistoryCleanupTask : Migration
{
    public override void Up()
    {
        // Runs after the 03:15 processed-message and 03:30 stale-media cleanups so the nightly
        // maintenance work stays sequential rather than contending for the same tables.
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'nightly-download-history-cleanup',
                    'download_history_cleanup',
                    '0 45 3 * * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    date_trunc('day', now()) + interval '1 day 3 hours 45 minutes'
                )
            ON CONFLICT ("key") DO UPDATE SET
                task_type = EXCLUDED.task_type,
                cron = EXCLUDED.cron,
                interval_seconds = NULL,
                timezone = EXCLUDED.timezone,
                enabled = EXCLUDED.enabled,
                catchup_policy = EXCLUDED.catchup_policy,
                next_due_at = COALESCE(scheduled_tasks.next_due_at, EXCLUDED.next_due_at),
                last_updated = now();
            """);
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE \"key\" = 'nightly-download-history-cleanup';");
    }
}
