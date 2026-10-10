using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Audio renditions are opus-only now: the per-request aac/mp3 format choice is gone, so the
/// format discriminator disappears from the rendition cache and from the playlist preference.
/// Non-opus rendition rows are dropped (their cached files on the old storage layout are stale
/// artifacts of the removed approach); opus rows keep serving from their recorded storage_path.
/// </summary>
[Migration(61, "Make audio renditions opus-only; drop format columns and enum")]
public sealed class M061_AudioRenditionsOpusOnly : Migration
{
    public override void Up()
    {
        Execute.Sql("DELETE FROM media.audio_renditions WHERE format <> 'opus';");

        Delete.Index("ux_audio_renditions_media_version_format_storage").OnTable("audio_renditions").InSchema("media");
        Delete.Column("format").FromTable("audio_renditions").InSchema("media");

        Create.Index("ux_audio_renditions_media_version_storage")
            .OnTable("audio_renditions").InSchema("media")
            .OnColumn("media_guid").Ascending()
            .OnColumn("source_version_num").Ascending()
            .OnColumn("storage_key").Ascending()
            .WithOptions().Unique();

        Delete.Column("audio_format").FromTable("playlists").InSchema("playlists");

        Execute.Sql("DROP TYPE IF EXISTS media.audio_rendition_format;");
    }

    public override void Down()
    {
        Execute.Sql("""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typnamespace = 'media'::regnamespace AND typname = 'audio_rendition_format') THEN
                    CREATE TYPE media.audio_rendition_format AS ENUM ('aac', 'opus');
                END IF;
            END $$;
            """);

        Alter.Table("playlists").InSchema("playlists")
            .AddColumn("audio_format").AsCustom("media.audio_rendition_format").NotNullable().WithDefaultValue("aac");

        Delete.Index("ux_audio_renditions_media_version_storage").OnTable("audio_renditions").InSchema("media");

        Alter.Table("audio_renditions").InSchema("media")
            .AddColumn("format").AsCustom("media.audio_rendition_format").NotNullable().WithDefaultValue("opus");

        Create.Index("ux_audio_renditions_media_version_format_storage")
            .OnTable("audio_renditions").InSchema("media")
            .OnColumn("media_guid").Ascending()
            .OnColumn("source_version_num").Ascending()
            .OnColumn("format").Ascending()
            .OnColumn("storage_key").Ascending()
            .WithOptions().Unique();
    }
}
