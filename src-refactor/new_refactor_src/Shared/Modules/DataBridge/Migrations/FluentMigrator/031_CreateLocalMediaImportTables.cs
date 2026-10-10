using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(31, "Create local media import tracking tables and ingest origin markers")]
public sealed class M031_CreateLocalMediaImportTables : Migration
{
    public override void Up()
    {
        Create.Schema("imports");

        Execute.Sql("CREATE TYPE media.ingest_origin AS ENUM ('download', 'local_import');");
        Execute.Sql("CREATE TYPE imports.local_import_status AS ENUM ('queued', 'preparing', 'uploading', 'completed', 'already_imported', 'failed');");

        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("ingest_origin").AsCustom("media.ingest_origin").NotNullable().WithDefaultValue("download");

        Alter.Table("media_content_id_versions").InSchema("media")
            .AddColumn("ingest_origin").AsCustom("media.ingest_origin").NotNullable().WithDefaultValue("download");

        Create.Table("local_import_batches").InSchema("imports")
            .WithColumn("batch_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("correlation_id").AsCustom("uuid").NotNullable()
            .WithColumn("status").AsCustom("imports.local_import_status").NotNullable()
            .WithColumn("manifest_object_bucket").AsString(255).NotNullable()
            .WithColumn("manifest_object_key").AsString(1024).NotNullable()
            .WithColumn("source_root").AsString(2048).NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("requested_by").AsString(255).Nullable()
            .WithColumn("requested_by_context").AsString(255).Nullable()
            .WithColumn("total_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("completed_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("already_imported_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("failed_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("error_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").Nullable();

        Create.Index("ix_local_import_batches_status_updated_at")
            .OnTable("local_import_batches").InSchema("imports")
            .OnColumn("status").Ascending()
            .OnColumn("updated_at").Ascending();

        Create.Table("local_import_items").InSchema("imports")
            .WithColumn("item_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("batch_id").AsCustom("uuid").NotNullable()
            .WithColumn("item_index").AsInt32().NotNullable()
            .WithColumn("status").AsCustom("imports.local_import_status").NotNullable()
            .WithColumn("source_root").AsString(2048).NotNullable()
            .WithColumn("relative_path").AsString(2048).NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("provider").AsString(255).Nullable()
            .WithColumn("source_media_id").AsString(512).Nullable()
            .WithColumn("source_last_modified").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("source_url").AsString(4096).Nullable()
            .WithColumn("title").AsString(1024).Nullable()
            .WithColumn("file_size_bytes").AsInt64().Nullable()
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

        Create.ForeignKey("fk_local_import_items_batch_id")
            .FromTable("local_import_items").InSchema("imports").ForeignColumn("batch_id")
            .ToTable("local_import_batches").InSchema("imports").PrimaryColumn("batch_id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_local_import_items_batch_item_index")
            .OnTable("local_import_items").InSchema("imports")
            .OnColumn("batch_id").Ascending()
            .OnColumn("item_index").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_local_import_items_batch_status")
            .OnTable("local_import_items").InSchema("imports")
            .OnColumn("batch_id").Ascending()
            .OnColumn("status").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_local_import_items_batch_status").OnTable("local_import_items").InSchema("imports");
        Delete.Index("ux_local_import_items_batch_item_index").OnTable("local_import_items").InSchema("imports");
        Delete.ForeignKey("fk_local_import_items_batch_id").OnTable("local_import_items").InSchema("imports");
        Delete.Table("local_import_items").InSchema("imports");

        Delete.Index("ix_local_import_batches_status_updated_at").OnTable("local_import_batches").InSchema("imports");
        Delete.Table("local_import_batches").InSchema("imports");

        Delete.Column("ingest_origin").FromTable("media_content_id_versions").InSchema("media");
        Delete.Column("ingest_origin").FromTable("download_jobs").InSchema("downloads");

        Execute.Sql("DROP TYPE IF EXISTS imports.local_import_status;");
        Execute.Sql("DROP TYPE IF EXISTS media.ingest_origin;");
        Delete.Schema("imports");
    }
}
