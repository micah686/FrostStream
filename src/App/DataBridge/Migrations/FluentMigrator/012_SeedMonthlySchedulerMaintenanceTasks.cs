using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(12, "Seed monthly scheduler maintenance tasks")]
public sealed class M012_SeedMonthlySchedulerMaintenanceTasks : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'monthly-db-maintenance',
                    'database_maintenance',
                    '0 0 3 1 * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    date_trunc('month', now()) + interval '1 month 3 hours'
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

        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'monthly-stale-media-cleanup',
                    'stale_database_cleanup',
                    '0 30 3 1 * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    date_trunc('month', now()) + interval '1 month 3 hours 30 minutes'
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
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE \"key\" IN ('monthly-db-maintenance', 'monthly-stale-media-cleanup');");
    }
}
