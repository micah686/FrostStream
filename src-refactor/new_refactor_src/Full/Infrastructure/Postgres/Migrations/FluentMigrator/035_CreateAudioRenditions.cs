using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(35, "Create cached audio rendition table")]
public sealed class M035_CreateAudioRenditions : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typnamespace = 'media'::regnamespace AND typname = 'audio_rendition_format') THEN
                    CREATE TYPE media.audio_rendition_format AS ENUM ('aac', 'opus');
                END IF;

                IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typnamespace = 'media'::regnamespace AND typname = 'audio_rendition_status') THEN
                    CREATE TYPE media.audio_rendition_status AS ENUM ('pending', 'processing', 'ready', 'failed');
                END IF;
            END $$;
            """);

        Create.Table("audio_renditions").InSchema("media")
            .WithColumn("rendition_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("source_version_num").AsInt32().NotNullable()
            .WithColumn("format").AsCustom("media.audio_rendition_format").NotNullable()
            .WithColumn("status").AsCustom("media.audio_rendition_status").NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("storage_path").AsString(2048).Nullable()
            .WithColumn("content_hash_xxh128").AsString(64).Nullable()
            .WithColumn("size_bytes").AsInt64().Nullable()
            .WithColumn("duration_seconds").AsInt32().Nullable()
            .WithColumn("error_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefault(SystemMethods.CurrentDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefault(SystemMethods.CurrentDateTime);

        Create.ForeignKey("fk_audio_renditions_source_version")
            .FromTable("audio_renditions").InSchema("media").ForeignColumns("media_guid", "source_version_num")
            .ToTable("media_content_id_versions").InSchema("media").PrimaryColumns("media_guid", "version_num")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_audio_renditions_media_version_format_storage")
            .OnTable("audio_renditions").InSchema("media")
            .OnColumn("media_guid").Ascending()
            .OnColumn("source_version_num").Ascending()
            .OnColumn("format").Ascending()
            .OnColumn("storage_key").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_audio_renditions_status")
            .OnTable("audio_renditions").InSchema("media")
            .OnColumn("status").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_audio_renditions_status").OnTable("audio_renditions").InSchema("media");
        Delete.Index("ux_audio_renditions_media_version_format_storage").OnTable("audio_renditions").InSchema("media");
        Delete.ForeignKey("fk_audio_renditions_source_version").OnTable("audio_renditions").InSchema("media");
        Delete.Table("audio_renditions").InSchema("media");
        Execute.Sql("DROP TYPE IF EXISTS media.audio_rendition_status;");
        Execute.Sql("DROP TYPE IF EXISTS media.audio_rendition_format;");
    }
}
