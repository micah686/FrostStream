using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(25, "Seed weekly channel_asset_refresh scheduler task")]
public sealed class M025_SeedWeeklyChannelAssetRefreshTask : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'weekly-channel-asset-refresh',
                    'channel_asset_refresh',
                    '0 0 4 ? * SUN',
                    'UTC',
                    true,
                    'Coalesce',
                    CASE
                        WHEN now() < date_trunc('week', now()) + interval '6 days 4 hours'
                            THEN date_trunc('week', now()) + interval '6 days 4 hours'
                        ELSE date_trunc('week', now()) + interval '13 days 4 hours'
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
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE \"key\" = 'weekly-channel-asset-refresh';");
    }
}
