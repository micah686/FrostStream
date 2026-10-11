using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(46, "Create orphan-cleanup policy")]
public sealed class M046_CreateOrphanCleanupPolicy : Migration
{
    public override void Up()
    {
        Execute.Sql("CREATE SCHEMA IF NOT EXISTS maintenance;");

        Execute.Sql("""
            CREATE TABLE IF NOT EXISTS maintenance.orphan_cleanup_policy
            (
                id smallint PRIMARY KEY,
                enabled boolean NOT NULL DEFAULT false,
                file_move_after_days integer NOT NULL DEFAULT 30,
                file_purge_after_days integer NOT NULL DEFAULT 30,
                metadata_delete_after_days integer NOT NULL DEFAULT 30,
                updated_by text NULL,
                updated_at timestamp with time zone NULL,
                last_run_at timestamp with time zone NULL,
                last_moved_count integer NOT NULL DEFAULT 0,
                last_deleted_files_count integer NOT NULL DEFAULT 0,
                last_deleted_metadata_count integer NOT NULL DEFAULT 0,
                CONSTRAINT ck_orphan_cleanup_policy_singleton CHECK (id = 1),
                CONSTRAINT ck_orphan_cleanup_policy_file_move_after_days CHECK (file_move_after_days > 0),
                CONSTRAINT ck_orphan_cleanup_policy_file_purge_after_days CHECK (file_purge_after_days > 0),
                CONSTRAINT ck_orphan_cleanup_policy_metadata_delete_after_days CHECK (metadata_delete_after_days > 0)
            );

            INSERT INTO maintenance.orphan_cleanup_policy
                (id, enabled, file_move_after_days, file_purge_after_days, metadata_delete_after_days)
            VALUES
                (1, false, 30, 30, 30)
            ON CONFLICT (id) DO NOTHING;
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP TABLE IF EXISTS maintenance.orphan_cleanup_policy;");
    }
}
