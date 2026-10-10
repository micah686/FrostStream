using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(69, "Remove watched-item auto-delete capability")]
public sealed class M069_RemoveWatchedAutoDelete : Migration
{
    public override void Up()
    {
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE task_type = 'watched_item_auto_delete' OR \"key\" = 'daily-watched-item-auto-delete';");
        Execute.Sql("DROP TABLE IF EXISTS maintenance.watched_item_auto_delete_policy;");
    }

    public override void Down()
    {
        Execute.Sql("CREATE SCHEMA IF NOT EXISTS maintenance;");

        Execute.Sql("""
            CREATE TABLE IF NOT EXISTS maintenance.watched_item_auto_delete_policy
            (
                id smallint PRIMARY KEY,
                enabled boolean NOT NULL DEFAULT false,
                delete_after_days integer NOT NULL DEFAULT 30,
                max_deletions_per_run integer NOT NULL DEFAULT 100,
                updated_by text NULL,
                updated_at timestamp with time zone NULL,
                last_run_at timestamp with time zone NULL,
                last_deleted_count integer NOT NULL DEFAULT 0,
                last_failed_count integer NOT NULL DEFAULT 0,
                CONSTRAINT ck_watched_auto_delete_singleton CHECK (id = 1),
                CONSTRAINT ck_watched_auto_delete_delete_after_days CHECK (delete_after_days > 0),
                CONSTRAINT ck_watched_auto_delete_max_deletions CHECK (max_deletions_per_run > 0)
            );

            INSERT INTO maintenance.watched_item_auto_delete_policy
                (id, enabled, delete_after_days, max_deletions_per_run)
            VALUES
                (1, false, 30, 100)
            ON CONFLICT (id) DO NOTHING;

            INSERT INTO scheduling.scheduled_tasks
                ("key", task_type, cron, timezone, enabled, catchup_policy, next_due_at)
            VALUES
                (
                    'daily-watched-item-auto-delete',
                    'watched_item_auto_delete',
                    '0 45 4 * * ?',
                    'UTC',
                    true,
                    'Coalesce',
                    date_trunc('day', now()) + interval '1 day 4 hours 45 minutes'
                )
            ON CONFLICT ("key") DO NOTHING;
            """);
    }
}
