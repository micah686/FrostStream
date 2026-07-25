using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Removes the orphan metadata/file cleanup feature: its lifecycle table (020), its retention
/// policy table (046), and the seeded nightly scheduled task (011). The `maintenance` schema
/// itself is retained for other maintenance data.
/// </summary>
[Migration(70, "Drop orphan cleanup feature")]
public sealed class M070_DropOrphanCleanupFeature : Migration
{
    public override void Up()
    {
        Delete.FromTable("scheduled_tasks").InSchema("scheduling").Row(new { key = "nightly-orphan-cleanup" });

        Execute.Sql("DROP TABLE IF EXISTS maintenance.orphan_cleanup_policy;");

        Delete.Index("ix_orphan_cleanup_items_media_guid").OnTable("orphan_cleanup_items").InSchema("maintenance");
        Delete.Index("ix_orphan_cleanup_items_state_delete_after").OnTable("orphan_cleanup_items").InSchema("maintenance");
        Execute.Sql("DROP INDEX IF EXISTS maintenance.uq_orphan_cleanup_items_identity;");
        Delete.Table("orphan_cleanup_items").InSchema("maintenance");
    }

    public override void Down()
    {
        Create.Table("orphan_cleanup_items").InSchema("maintenance")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("item_kind").AsString(64).NotNullable()
            .WithColumn("state").AsString(64).NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("original_storage_path").AsString(2048).NotNullable()
            .WithColumn("orphan_storage_path").AsString(2048).Nullable()
            .WithColumn("media_guid").AsCustom("uuid").Nullable()
            .WithColumn("detected_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("last_seen_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("delete_after").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("moved_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("finalized_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("resolved_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_error").AsCustom("text").Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable();

        Execute.Sql("""
            CREATE UNIQUE INDEX uq_orphan_cleanup_items_identity
            ON maintenance.orphan_cleanup_items (
                item_kind,
                storage_key,
                original_storage_path,
                COALESCE(media_guid, '00000000-0000-0000-0000-000000000000'::uuid)
            );
            """);

        Create.Index("ix_orphan_cleanup_items_state_delete_after")
            .OnTable("orphan_cleanup_items").InSchema("maintenance")
            .OnColumn("state").Ascending()
            .OnColumn("delete_after").Ascending();

        Create.Index("ix_orphan_cleanup_items_media_guid")
            .OnTable("orphan_cleanup_items").InSchema("maintenance")
            .OnColumn("media_guid").Ascending();

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

        Insert.IntoTable("scheduled_tasks").InSchema("scheduling").Row(new
        {
            key = "nightly-orphan-cleanup",
            task_type = "orphan_metadata_cleanup",
            cron = "0 0 3 * * ?",
            timezone = "UTC",
            enabled = false,
            catchup_policy = "Coalesce"
        });
    }
}
