using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(48, "Seed daily channel_media_list full-rescan sweep task")]
public sealed class M048_SeedDailyChannelFullRescanTask : Migration
{
    public override void Up()
    {
        // Each firing publishes a ChannelMediaListRequested sweep (no TargetSourceId);
        // DataBridge then returns only the sources whose LastFullScanAt + FullRescanIntervalDays
        // is due, so per-source intervals are honored without per-source Quartz jobs.
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'daily-channel-full-rescan',
                    'channel_media_list',
                    '0 30 3 * * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    CASE
                        WHEN now() < date_trunc('day', now()) + interval '3 hours 30 minutes'
                            THEN date_trunc('day', now()) + interval '3 hours 30 minutes'
                        ELSE date_trunc('day', now()) + interval '1 day 3 hours 30 minutes'
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
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE \"key\" = 'daily-channel-full-rescan';");
    }
}
