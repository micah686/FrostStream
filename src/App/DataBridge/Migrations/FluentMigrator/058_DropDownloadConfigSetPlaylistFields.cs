using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// These three fields duplicated settings the Playlist/CreatorSource request bodies already carry
/// directly (see DownloadConfigSetResolver, which only ever used them as a fallback default) and
/// were removed from the config-set UI/API in favor of always specifying them per-request.
/// </summary>
[Migration(58, "Drop encode_for_playlist/audio_format/fetch_comments from download_config_sets")]
public sealed class M058_DropDownloadConfigSetPlaylistFields : Migration
{
    public override void Up()
    {
        Delete.Column("encode_for_playlist").FromTable("download_config_sets").InSchema("downloads");
        Delete.Column("audio_format").FromTable("download_config_sets").InSchema("downloads");
        Delete.Column("fetch_comments").FromTable("download_config_sets").InSchema("downloads");
    }

    public override void Down()
    {
        Alter.Table("download_config_sets").InSchema("downloads")
            .AddColumn("encode_for_playlist").AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Table("download_config_sets").InSchema("downloads")
            .AddColumn("audio_format").AsCustom("media.audio_rendition_format").NotNullable().WithDefaultValue("aac");

        Alter.Table("download_config_sets").InSchema("downloads")
            .AddColumn("fetch_comments").AsBoolean().NotNullable().WithDefaultValue(false);
    }
}
