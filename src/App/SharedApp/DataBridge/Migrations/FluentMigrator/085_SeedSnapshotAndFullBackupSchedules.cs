using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(85, "Seed snapshot and full backup schedules")]
public sealed class M085_SeedSnapshotAndFullBackupSchedules : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            UPDATE scheduling.scheduled_tasks
            SET
                "key" = 'backup-snapshot',
                task_type = 'backup-snapshot',
                enabled = true,
                last_updated = now()
            WHERE "key" = 'nightly-backup'
              AND NOT EXISTS (
                  SELECT 1
                  FROM scheduling.scheduled_tasks existing
                  WHERE existing."key" = 'backup-snapshot'
              );

            DELETE FROM scheduling.scheduled_tasks
            WHERE "key" = 'nightly-backup';
            """);

        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'backup-snapshot',
                    'backup-snapshot',
                    '0 0 2 * * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    CASE
                        WHEN now() < date_trunc('day', now()) + interval '2 hours'
                            THEN date_trunc('day', now()) + interval '2 hours'
                        ELSE date_trunc('day', now()) + interval '1 day 2 hours'
                    END
                ),
                (
                    'backup-full',
                    'backup-full',
                    '0 0 3 ? * SUN',
                    'UTC',
                    true,
                    'Coalesce',
                    CASE
                        WHEN now() < date_trunc('week', now()) + interval '6 days 3 hours'
                            THEN date_trunc('week', now()) + interval '6 days 3 hours'
                        ELSE date_trunc('week', now()) + interval '13 days 3 hours'
                    END
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
        Execute.Sql("""
            DELETE FROM scheduling.scheduled_tasks
            WHERE "key" = 'backup-full';

            UPDATE scheduling.scheduled_tasks
            SET
                "key" = 'nightly-backup',
                task_type = 'backup',
                last_updated = now()
            WHERE "key" = 'backup-snapshot';
            """);
    }
}
