using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(5, TransactionBehavior.None, "Create media source/content version tables for download idempotency")]
public sealed class M005_CreateMediaVersionTables : Migration
{
    private const string SchemaName = "media";
    private const string DownloadsSchema = "downloads";

    public override void Up()
    {
        Create.Schema(SchemaName);

        Execute.Sql("ALTER TYPE downloads.download_job_state ADD VALUE IF NOT EXISTS 'already_downloaded';");

        Create.Table("media_source_versions").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("provider").AsString(255).Nullable()
            .WithColumn("source_media_id").AsString(512).Nullable()
            .WithColumn("source_last_modified").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("latest_job_id").AsCustom("uuid").Nullable();

        Create.Index("ix_media_source_versions_provider_source_media_id")
            .OnTable("media_source_versions").InSchema(SchemaName)
            .OnColumn("provider").Ascending()
            .OnColumn("source_media_id").Ascending();

        Create.Index("ix_media_source_versions_media_guid")
            .OnTable("media_source_versions").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending();

        Create.ForeignKey("fk_media_source_versions_latest_job_id")
            .FromTable("media_source_versions").InSchema(SchemaName).ForeignColumn("latest_job_id")
            .ToTable("download_jobs").InSchema(DownloadsSchema).PrimaryColumn("job_id")
            .OnDelete(Rule.SetNull);

        Create.Table("media_content_id_versions").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("content_hash_xxh128").AsString(64).NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("storage_path").AsString(2048).NotNullable()
            .WithColumn("version_num").AsInt32().NotNullable();

        Create.Index("ux_media_content_id_versions_media_guid_version_num")
            .OnTable("media_content_id_versions").InSchema(SchemaName)
            .OnColumn("media_guid").Ascending()
            .OnColumn("version_num").Ascending()
            .WithOptions().Unique();

        Create.Index("ux_media_content_id_versions_storage_key_content_hash")
            .OnTable("media_content_id_versions").InSchema(SchemaName)
            .OnColumn("storage_key").Ascending()
            .OnColumn("content_hash_xxh128").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Index("ux_media_content_id_versions_storage_key_content_hash").OnTable("media_content_id_versions").InSchema(SchemaName);
        Delete.Index("ux_media_content_id_versions_media_guid_version_num").OnTable("media_content_id_versions").InSchema(SchemaName);
        Delete.Table("media_content_id_versions").InSchema(SchemaName);

        Delete.ForeignKey("fk_media_source_versions_latest_job_id").OnTable("media_source_versions").InSchema(SchemaName);
        Delete.Index("ix_media_source_versions_media_guid").OnTable("media_source_versions").InSchema(SchemaName);
        Delete.Index("ix_media_source_versions_provider_source_media_id").OnTable("media_source_versions").InSchema(SchemaName);
        Delete.Table("media_source_versions").InSchema(SchemaName);

        // PostgreSQL enum labels cannot be dropped safely; leave already_downloaded in place.
        // This migration owns the `media` schema; M007's Down (run earlier) already dropped
        // the media root table, so the schema is empty and safe to drop here.
        Delete.Schema(SchemaName);
    }
}
