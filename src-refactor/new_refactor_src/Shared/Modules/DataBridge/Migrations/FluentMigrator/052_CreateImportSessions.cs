using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(52, "Create local media import sessions")]
public sealed class M052_CreateImportSessions : Migration
{
    public override void Up()
    {
        Execute.Sql("CREATE TYPE imports.import_session_status AS ENUM ('scanning', 'scan_failed', 'reviewing', 'committing', 'completed', 'completed_with_failures', 'cancelled');");
        Execute.Sql("CREATE TYPE imports.import_session_source_kind AS ENUM ('worker_incoming', 'storage_backend');");
        Execute.Sql("CREATE TYPE imports.import_session_item_status AS ENUM ('discovered', 'probed', 'approved', 'hashing', 'uploading', 'finalizing', 'imported', 'already_imported', 'failed');");
        Execute.Sql("CREATE TYPE imports.import_session_item_metadata_state AS ENUM ('incomplete', 'ready', 'edited', 'placeholder_accepted');");

        Create.Table("import_sessions").InSchema("imports")
            .WithColumn("session_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("correlation_id").AsCustom("uuid").NotNullable()
            .WithColumn("status").AsCustom("imports.import_session_status").NotNullable()
            .WithColumn("source_kind").AsCustom("imports.import_session_source_kind").NotNullable()
            .WithColumn("source_root").AsString(2048).NotNullable()
            .WithColumn("sub_path").AsString(2048).Nullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("worker_tag").AsString(128).Nullable()
            .WithColumn("requested_by").AsString(255).Nullable()
            .WithColumn("total_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("probed_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("ready_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("incomplete_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("excluded_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("approved_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("imported_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("already_imported_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("failed_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("max_parallel_items").AsInt32().NotNullable().WithDefaultValue(6)
            .WithColumn("error_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").Nullable();

        Create.Index("ix_import_sessions_status_updated_at")
            .OnTable("import_sessions").InSchema("imports")
            .OnColumn("status").Ascending()
            .OnColumn("updated_at").Descending();

        Create.Table("import_session_items").InSchema("imports")
            .WithColumn("item_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("session_id").AsCustom("uuid").NotNullable()
            .WithColumn("relative_path").AsString(2048).NotNullable()
            .WithColumn("file_name").AsString(1024).NotNullable()
            .WithColumn("file_size_bytes").AsInt64().NotNullable()
            .WithColumn("file_mtime").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("sidecars").AsCustom("jsonb").Nullable()
            .WithColumn("provider").AsString(255).Nullable()
            .WithColumn("source_media_id").AsString(512).Nullable()
            .WithColumn("source_url").AsString(4096).Nullable()
            .WithColumn("title").AsString(1024).Nullable()
            .WithColumn("probe_metadata").AsCustom("jsonb").Nullable()
            .WithColumn("scan_metadata").AsCustom("jsonb").Nullable()
            .WithColumn("enriched_metadata").AsCustom("jsonb").Nullable()
            .WithColumn("user_metadata").AsCustom("jsonb").Nullable()
            .WithColumn("metadata_state").AsCustom("imports.import_session_item_metadata_state").NotNullable()
            .WithColumn("excluded").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("status").AsCustom("imports.import_session_item_status").NotNullable()
            .WithColumn("attempt").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("content_hash_xxh128").AsString(64).Nullable()
            .WithColumn("media_guid").AsCustom("uuid").Nullable()
            .WithColumn("storage_path").AsString(2048).Nullable()
            .WithColumn("storage_version").AsString(255).Nullable()
            .WithColumn("meta_storage_path").AsString(2048).Nullable()
            .WithColumn("info_json_storage_path").AsString(2048).Nullable()
            .WithColumn("thumbnail_storage_path").AsString(2048).Nullable()
            .WithColumn("caption_storage_paths").AsCustom("jsonb").Nullable()
            .WithColumn("error_code").AsString(255).Nullable()
            .WithColumn("error_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").Nullable();

        Create.ForeignKey("fk_import_session_items_session_id")
            .FromTable("import_session_items").InSchema("imports").ForeignColumn("session_id")
            .ToTable("import_sessions").InSchema("imports").PrimaryColumn("session_id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_import_session_items_session_relative_path")
            .OnTable("import_session_items").InSchema("imports")
            .OnColumn("session_id").Ascending()
            .OnColumn("relative_path").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_import_session_items_session_status")
            .OnTable("import_session_items").InSchema("imports")
            .OnColumn("session_id").Ascending()
            .OnColumn("status").Ascending();

        Execute.Sql("""
            CREATE INDEX ix_import_session_items_session_metadata_state
            ON imports.import_session_items (session_id, metadata_state)
            WHERE excluded = false;
            """);

        Create.Index("ix_import_session_items_session_item_id")
            .OnTable("import_session_items").InSchema("imports")
            .OnColumn("session_id").Ascending()
            .OnColumn("item_id").Ascending();

        Create.Table("import_session_mappings").InSchema("imports")
            .WithColumn("mapping_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("session_id").AsCustom("uuid").NotNullable()
            .WithColumn("object_bucket").AsString(255).NotNullable()
            .WithColumn("object_key").AsString(1024).NotNullable()
            .WithColumn("format").AsString(32).NotNullable()
            .WithColumn("matched_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("unmatched_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("applied_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.ForeignKey("fk_import_session_mappings_session_id")
            .FromTable("import_session_mappings").InSchema("imports").ForeignColumn("session_id")
            .ToTable("import_sessions").InSchema("imports").PrimaryColumn("session_id")
            .OnDelete(System.Data.Rule.Cascade);
    }

    public override void Down()
    {
        Delete.ForeignKey("fk_import_session_mappings_session_id").OnTable("import_session_mappings").InSchema("imports");
        Delete.Table("import_session_mappings").InSchema("imports");

        Delete.Index("ix_import_session_items_session_item_id").OnTable("import_session_items").InSchema("imports");
        Delete.Index("ix_import_session_items_session_metadata_state").OnTable("import_session_items").InSchema("imports");
        Delete.Index("ix_import_session_items_session_status").OnTable("import_session_items").InSchema("imports");
        Delete.Index("ux_import_session_items_session_relative_path").OnTable("import_session_items").InSchema("imports");
        Delete.ForeignKey("fk_import_session_items_session_id").OnTable("import_session_items").InSchema("imports");
        Delete.Table("import_session_items").InSchema("imports");

        Delete.Index("ix_import_sessions_status_updated_at").OnTable("import_sessions").InSchema("imports");
        Delete.Table("import_sessions").InSchema("imports");

        Execute.Sql("DROP TYPE IF EXISTS imports.import_session_item_metadata_state;");
        Execute.Sql("DROP TYPE IF EXISTS imports.import_session_item_status;");
        Execute.Sql("DROP TYPE IF EXISTS imports.import_session_source_kind;");
        Execute.Sql("DROP TYPE IF EXISTS imports.import_session_status;");
    }
}
