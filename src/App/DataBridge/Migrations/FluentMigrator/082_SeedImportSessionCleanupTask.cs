using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(82, "Seed nightly import_session_cleanup scheduler task")]
public sealed class M082_SeedImportSessionCleanupTask : Migration
{
    public override void Up()
    {
        // Runs after the 03:45 download-history cleanup so the nightly maintenance work stays
        // sequential rather than contending for the same tables.
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, retention_days, include_failed, next_due_at)
            VALUES
                (
                    'nightly-import-session-cleanup',
                    'import_session_cleanup',
                    '0 0 4 * * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    30,
                    false,
                    date_trunc('day', now()) + interval '1 day 4 hours'
                )
            ON CONFLICT ("key") DO UPDATE SET
                task_type = EXCLUDED.task_type,
                cron = EXCLUDED.cron,
                interval_seconds = NULL,
                timezone = EXCLUDED.timezone,
                enabled = EXCLUDED.enabled,
                catchup_policy = EXCLUDED.catchup_policy,
                retention_days = EXCLUDED.retention_days,
                include_failed = EXCLUDED.include_failed,
                next_due_at = COALESCE(scheduled_tasks.next_due_at, EXCLUDED.next_due_at),
                last_updated = now();
            """);
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE \"key\" = 'nightly-import-session-cleanup';");
    }
}
