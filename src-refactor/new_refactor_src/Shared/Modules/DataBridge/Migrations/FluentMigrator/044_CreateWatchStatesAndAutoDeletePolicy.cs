using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(44, "Create watch states and watched-item auto-delete policy")]
public sealed class M044_CreateWatchStatesAndAutoDeletePolicy : Migration
{
    public override void Up()
    {
        Execute.Sql("CREATE SCHEMA IF NOT EXISTS maintenance;");

        Execute.Sql("""
            CREATE TABLE IF NOT EXISTS media.watch_states
            (
                owner_subject text NOT NULL,
                media_guid uuid NOT NULL,
                position_seconds double precision NULL,
                duration_seconds double precision NULL,
                completed boolean NOT NULL DEFAULT false,
                watched_at timestamp with time zone NULL,
                last_played_at timestamp with time zone NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT now(),
                updated_at timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT pk_watch_states PRIMARY KEY (owner_subject, media_guid),
                CONSTRAINT fk_watch_states_media_guid
                    FOREIGN KEY (media_guid)
                    REFERENCES media.media (media_guid)
                    ON DELETE CASCADE,
                CONSTRAINT ck_watch_states_position_nonnegative
                    CHECK (position_seconds IS NULL OR position_seconds >= 0),
                CONSTRAINT ck_watch_states_duration_positive
                    CHECK (duration_seconds IS NULL OR duration_seconds > 0)
            );

            CREATE INDEX IF NOT EXISTS ix_watch_states_completed_watched_at
                ON media.watch_states (completed, watched_at)
                WHERE completed = true AND watched_at IS NOT NULL;

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
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE \"key\" = 'daily-watched-item-auto-delete';");
        Execute.Sql("DROP TABLE IF EXISTS maintenance.watched_item_auto_delete_policy;");
        Execute.Sql("DROP TABLE IF EXISTS media.watch_states;");
    }
}

