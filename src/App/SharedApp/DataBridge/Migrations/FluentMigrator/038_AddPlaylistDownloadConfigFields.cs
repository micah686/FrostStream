using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(38, "Add resolved download config fields to playlists")]
public sealed class M038_AddPlaylistDownloadConfigFields : Migration
{
    public override void Up()
    {
        Alter.Table("playlists").InSchema("playlists")
            .AddColumn("config_set_key").AsString(100).Nullable()
            .AddColumn("cookie_secret_path").AsString(512).Nullable()
            .AddColumn("ytdlp_options_json").AsCustom("jsonb").Nullable()
            .AddColumn("priority").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("fetch_comments").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("fetch_comments").FromTable("playlists").InSchema("playlists");
        Delete.Column("priority").FromTable("playlists").InSchema("playlists");
        Delete.Column("ytdlp_options_json").FromTable("playlists").InSchema("playlists");
        Delete.Column("cookie_secret_path").FromTable("playlists").InSchema("playlists");
        Delete.Column("config_set_key").FromTable("playlists").InSchema("playlists");
    }
}
