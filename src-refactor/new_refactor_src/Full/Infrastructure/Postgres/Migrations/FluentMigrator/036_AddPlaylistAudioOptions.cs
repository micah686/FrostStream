using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(36, "Add playlist audio stream options")]
public sealed class M036_AddPlaylistAudioOptions : Migration
{
    public override void Up()
    {
        Alter.Table("playlists").InSchema("playlists")
            .AddColumn("encode_for_playlist").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("audio_format").AsCustom("media.audio_rendition_format").NotNullable().WithDefaultValue("aac");
    }

    public override void Down()
    {
        Delete.Column("audio_format").FromTable("playlists").InSchema("playlists");
        Delete.Column("encode_for_playlist").FromTable("playlists").InSchema("playlists");
    }
}
