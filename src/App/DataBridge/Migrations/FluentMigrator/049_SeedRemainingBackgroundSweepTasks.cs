using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(49, "Seed channel_update_check, filesystem_rescan, search_reindex and backup scheduler tasks")]
public sealed class M049_SeedRemainingBackgroundSweepTasks : Migration
{
    public override void Up()
    {
        // Incremental creator-source scan sweep. Each firing publishes a
        // ChannelUpdateCheckRequested; the Worker scans every enabled source using its
        // IncrementalPageSize/ConsecutiveKnownThreshold settings.
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'channel-update-check',
                    'channel_update_check',
                    '0 0 */6 * * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    date_trunc('day', now())
                        + (floor(extract(hour from now()) / 6) + 1) * interval '6 hours'
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

        // Weekly filesystem reconciliation sweep across all storage keys (Saturday 05:00 UTC).
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'weekly-filesystem-rescan',
                    'filesystem_rescan',
                    '0 0 5 ? * SAT',
                    'UTC',
                    true,
                    'Coalesce',
                    CASE
                        WHEN now() < date_trunc('week', now()) + interval '5 days 5 hours'
                            THEN date_trunc('week', now()) + interval '5 days 5 hours'
                        ELSE date_trunc('week', now()) + interval '12 days 5 hours'
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

        // Weekly full Typesense index rebuild (Sunday 05:30 UTC, after the 04:00 asset refresh).
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'weekly-search-reindex',
                    'search_reindex',
                    '0 30 5 ? * SUN',
                    'UTC',
                    true,
                    'Coalesce',
                    CASE
                        WHEN now() < date_trunc('week', now()) + interval '6 days 5 hours 30 minutes'
                            THEN date_trunc('week', now()) + interval '6 days 5 hours 30 minutes'
                        ELSE date_trunc('week', now()) + interval '13 days 5 hours 30 minutes'
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

        // Nightly database backup (02:00 UTC). Seeded disabled — mirrors nightly-orphan-cleanup —
        // because backups are an operator-controlled retention policy; enable through the schedules
        // API after confirming the BackupService host bind has sufficient capacity.
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'nightly-backup',
                    'backup',
                    '0 0 2 * * ?',
                    'UTC',
                    false,
                    'Coalesce',
                    CASE
                        WHEN now() < date_trunc('day', now()) + interval '2 hours'
                            THEN date_trunc('day', now()) + interval '2 hours'
                        ELSE date_trunc('day', now()) + interval '1 day 2 hours'
                    END
                )
            ON CONFLICT ("key") DO UPDATE SET
                task_type = EXCLUDED.task_type,
                cron = EXCLUDED.cron,
                interval_seconds = NULL,
                timezone = EXCLUDED.timezone,
                catchup_policy = EXCLUDED.catchup_policy,
                next_due_at = COALESCE(scheduled_tasks.next_due_at, EXCLUDED.next_due_at),
                last_updated = now();
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE FROM scheduling.scheduled_tasks
            WHERE "key" IN ('channel-update-check', 'weekly-filesystem-rescan', 'weekly-search-reindex', 'nightly-backup');
            """);
    }
}
