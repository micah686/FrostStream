using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(17, "Seed nightly processed_message_cleanup scheduler task")]
public sealed class M017_SeedProcessedMessageCleanupTask : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            INSERT INTO scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'nightly-processed-message-cleanup',
                    'processed_message_cleanup',
                    '0 15 3 * * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    date_trunc('day', now()) + interval '1 day 3 hours 15 minutes'
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
        Execute.Sql("DELETE FROM scheduled_tasks WHERE \"key\" = 'nightly-processed-message-cleanup';");
    }
}
