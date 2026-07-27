using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(71, "Seed one schedule for each missing registered task type")]
public sealed class M071_SeedMissingScheduledTaskTypes : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            WITH defaults("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at) AS (
                VALUES
                    (
                        'channel-scan-refresh',
                        'channel_update_check',
                        '0 0 */6 * * ?',
                        'UTC',
                        true,
                        'Coalesce',
                        date_trunc('day', now()) + (floor(extract(hour from now()) / 6) + 1) * interval '6 hours'
                    ),
                    (
                        'channel-asset-refresh',
                        'channel-asset-refresh',
                        '0 0 4 ? * SUN',
                        'UTC',
                        true,
                        'Coalesce',
                        CASE
                            WHEN now() < date_trunc('week', now()) + interval '6 days 4 hours'
                                THEN date_trunc('week', now()) + interval '6 days 4 hours'
                            ELSE date_trunc('week', now()) + interval '13 days 4 hours'
                        END
                    ),
                    (
                        'channel-full-rescan',
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
                    ),
                    (
                        'db-stale-media-cleanup',
                        'db-stale-media-cleanup',
                        '0 30 3 1 * ?',
                        'UTC',
                        true,
                        'Coalesce',
                        date_trunc('month', now()) + interval '1 month 3 hours 30 minutes'
                    ),
                    (
                        'db-maintenance',
                        'db-maintenance',
                        '0 0 3 1 * ?',
                        'UTC',
                        true,
                        'Coalesce',
                        date_trunc('month', now()) + interval '1 month 3 hours'
                    ),
                    (
                        'search-reindex',
                        'search-reindex',
                        '0 30 5 ? * SUN',
                        'UTC',
                        true,
                        'Coalesce',
                        CASE
                            WHEN now() < date_trunc('week', now()) + interval '6 days 5 hours 30 minutes'
                                THEN date_trunc('week', now()) + interval '6 days 5 hours 30 minutes'
                            ELSE date_trunc('week', now()) + interval '13 days 5 hours 30 minutes'
                        END
                    ),
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
                    ),
                    (
                        'nightly-processed-message-cleanup',
                        'processed_message_cleanup',
                        '0 15 3 * * ?',
                        'UTC',
                        true,
                        'Coalesce',
                        CASE
                            WHEN now() < date_trunc('day', now()) + interval '3 hours 15 minutes'
                                THEN date_trunc('day', now()) + interval '3 hours 15 minutes'
                            ELSE date_trunc('day', now()) + interval '1 day 3 hours 15 minutes'
                        END
                    ),
                    (
                        'backup-snapshot',
                        'backup-snapshot',
                        '0 0 2 * * ?',
                        'UTC',
                        true,
                        'Coalesce',
                        NULL::timestamp with time zone
                    )
            )
            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            SELECT
                defaults."key",
                defaults.task_type,
                defaults.cron,
                defaults.timezone,
                defaults.enabled,
                defaults.catchup_policy,
                defaults.next_due_at
            FROM defaults
            WHERE NOT EXISTS (
                SELECT 1
                FROM scheduling.scheduled_tasks existing
                WHERE existing.task_type = defaults.task_type
            )
            ON CONFLICT ("key") DO NOTHING;
            """);
    }

    public override void Down()
    {
        // Intentionally no-op. This migration only fills missing task types and cannot
        // distinguish its inserted rows from schedules seeded by older migrations or
        // operator-created replacement rows.
    }
}
