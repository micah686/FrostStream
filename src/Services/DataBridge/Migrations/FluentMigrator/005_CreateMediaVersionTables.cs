using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(5, TransactionBehavior.None, "Create media source/content version tables for download idempotency")]
public sealed class M005_CreateMediaVersionTables : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TYPE download_job_state ADD VALUE IF NOT EXISTS 'already_downloaded';");

        Alter.Table("download_jobs")
            .AddColumn("source_metadata_hash").AsString(64).Nullable();

        Create.Index("ix_download_jobs_source_metadata_hash")
            .OnTable("download_jobs")
            .OnColumn("source_metadata_hash").Ascending();

        Create.Table("media_content_id_versions")
            .WithColumn("content_hash_xxh128").AsString(64).PrimaryKey()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("path").AsString(2048).NotNullable();

        Create.Index("ix_media_content_id_versions_storage_key_path")
            .OnTable("media_content_id_versions")
            .OnColumn("storage_key").Ascending()
            .OnColumn("path").Ascending();

        Create.Table("media_source_versions")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("source_metadata_hash").AsString(64).NotNullable()
            .WithColumn("provider").AsString(255).Nullable()
            .WithColumn("source_media_id").AsString(512).Nullable()
            .WithColumn("source_last_modified").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("latest_content_hash_xxh128").AsString(64).Nullable()
            .WithColumn("latest_job_id").AsCustom("uuid").Nullable();

        Create.Index("ux_media_source_versions_source_metadata_hash")
            .OnTable("media_source_versions")
            .OnColumn("source_metadata_hash").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_media_source_versions_latest_content_hash_xxh128")
            .OnTable("media_source_versions")
            .OnColumn("latest_content_hash_xxh128").Ascending();

        Create.ForeignKey("fk_media_source_versions_latest_content_hash_xxh128")
            .FromTable("media_source_versions").ForeignColumn("latest_content_hash_xxh128")
            .ToTable("media_content_id_versions").PrimaryColumn("content_hash_xxh128")
            .OnDelete(Rule.SetNull);

        Create.ForeignKey("fk_media_source_versions_latest_job_id")
            .FromTable("media_source_versions").ForeignColumn("latest_job_id")
            .ToTable("download_jobs").PrimaryColumn("job_id")
            .OnDelete(Rule.SetNull);
    }

    public override void Down()
    {
        Delete.ForeignKey("fk_media_source_versions_latest_job_id").OnTable("media_source_versions");
        Delete.ForeignKey("fk_media_source_versions_latest_content_hash_xxh128").OnTable("media_source_versions");
        Delete.Index("ix_media_source_versions_latest_content_hash_xxh128").OnTable("media_source_versions");
        Delete.Index("ux_media_source_versions_source_metadata_hash").OnTable("media_source_versions");
        Delete.Table("media_source_versions");

        Delete.Index("ix_media_content_id_versions_storage_key_path").OnTable("media_content_id_versions");
        Delete.Table("media_content_id_versions");

        Delete.Index("ix_download_jobs_source_metadata_hash").OnTable("download_jobs");
        Delete.Column("source_metadata_hash").FromTable("download_jobs");

        // PostgreSQL enum labels cannot be dropped safely; leave already_downloaded in place.
    }
}
