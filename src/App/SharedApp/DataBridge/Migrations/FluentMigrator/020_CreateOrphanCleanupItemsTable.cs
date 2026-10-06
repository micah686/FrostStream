using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(20, "Create orphan cleanup lifecycle table")]
public sealed class M020_CreateOrphanCleanupItemsTable : Migration
{
    private const string SchemaName = "maintenance";

    public override void Up()
    {
        Create.Table("orphan_cleanup_items").InSchema(SchemaName)
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
            .OnTable("orphan_cleanup_items").InSchema(SchemaName)
            .OnColumn("state").Ascending()
            .OnColumn("delete_after").Ascending();

        Create.Index("ix_orphan_cleanup_items_media_guid")
            .OnTable("orphan_cleanup_items").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_orphan_cleanup_items_media_guid").OnTable("orphan_cleanup_items").InSchema(SchemaName);
        Delete.Index("ix_orphan_cleanup_items_state_delete_after").OnTable("orphan_cleanup_items").InSchema(SchemaName);
        Execute.Sql("DROP INDEX IF EXISTS maintenance.uq_orphan_cleanup_items_identity;");
        Delete.Table("orphan_cleanup_items").InSchema(SchemaName);
    }
}
