using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(33, "Create owner-scoped user playlists")]
public sealed class M033_CreateUserPlaylists : Migration
{
    private const string PlaylistsSchema = "playlists";
    private const string MediaSchema = "media";

    public override void Up()
    {
        Create.Table("user_playlists").InSchema(PlaylistsSchema)
            .WithColumn("playlist_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("owner_subject").AsString(255).NotNullable()
            .WithColumn("name").AsString(255).NotNullable()
            .WithColumn("description").AsString(2048).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_user_playlists_owner_created_at")
            .OnTable("user_playlists").InSchema(PlaylistsSchema)
            .OnColumn("owner_subject").Ascending()
            .OnColumn("created_at").Descending();

        Create.Table("user_playlist_items").InSchema(PlaylistsSchema)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("media_guid").AsCustom("uuid").NotNullable()
            .WithColumn("position").AsInt32().NotNullable()
            .WithColumn("added_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.ForeignKey("fk_user_playlist_items_playlist_id")
            .FromTable("user_playlist_items").InSchema(PlaylistsSchema).ForeignColumn("playlist_id")
            .ToTable("user_playlists").InSchema(PlaylistsSchema).PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("fk_user_playlist_items_media_guid")
            .FromTable("user_playlist_items").InSchema(PlaylistsSchema).ForeignColumn("media_guid")
            .ToTable("media").InSchema(MediaSchema).PrimaryColumn("media_guid")
            .OnDelete(Rule.Cascade);

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_user_playlist_items_playlist_position " +
            "ON playlists.user_playlist_items (playlist_id, position);");

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_user_playlist_items_playlist_media " +
            "ON playlists.user_playlist_items (playlist_id, media_guid);");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS playlists.ux_user_playlist_items_playlist_media;");
        Execute.Sql("DROP INDEX IF EXISTS playlists.ux_user_playlist_items_playlist_position;");
        Delete.ForeignKey("fk_user_playlist_items_media_guid").OnTable("user_playlist_items").InSchema(PlaylistsSchema);
        Delete.ForeignKey("fk_user_playlist_items_playlist_id").OnTable("user_playlist_items").InSchema(PlaylistsSchema);
        Delete.Table("user_playlist_items").InSchema(PlaylistsSchema);

        Delete.Index("ix_user_playlists_owner_created_at").OnTable("user_playlists").InSchema(PlaylistsSchema);
        Delete.Table("user_playlists").InSchema(PlaylistsSchema);
    }
}
