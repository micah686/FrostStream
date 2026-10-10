using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(60, "Create cached stream (video HLS) rendition table")]
public sealed class M060_CreateStreamRenditions : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typnamespace = 'media'::regnamespace AND typname = 'stream_rendition_status') THEN
                    CREATE TYPE media.stream_rendition_status AS ENUM ('pending', 'processing', 'ready', 'failed');
                END IF;
            END $$;
            """);

        Create.Table("stream_renditions").InSchema("media")
            .WithColumn("rendition_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("source_version_num").AsInt32().NotNullable()
            .WithColumn("status").AsCustom("media.stream_rendition_status").NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("storage_path").AsString(2048).Nullable()
            .WithColumn("size_bytes").AsInt64().Nullable()
            .WithColumn("duration_seconds").AsInt32().Nullable()
            .WithColumn("error_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefault(SystemMethods.CurrentDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefault(SystemMethods.CurrentDateTime);

        Create.ForeignKey("fk_stream_renditions_source_version")
            .FromTable("stream_renditions").InSchema("media").ForeignColumns("media_guid", "source_version_num")
            .ToTable("media_content_id_versions").InSchema("media").PrimaryColumns("media_guid", "version_num")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_stream_renditions_media_version_storage")
            .OnTable("stream_renditions").InSchema("media")
            .OnColumn("media_guid").Ascending()
            .OnColumn("source_version_num").Ascending()
            .OnColumn("storage_key").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_stream_renditions_status")
            .OnTable("stream_renditions").InSchema("media")
            .OnColumn("status").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_stream_renditions_status").OnTable("stream_renditions").InSchema("media");
        Delete.Index("ux_stream_renditions_media_version_storage").OnTable("stream_renditions").InSchema("media");
        Delete.ForeignKey("fk_stream_renditions_source_version").OnTable("stream_renditions").InSchema("media");
        Delete.Table("stream_renditions").InSchema("media");
        Execute.Sql("DROP TYPE IF EXISTS media.stream_rendition_status;");
    }
}
