using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(9, "Create playlists.playlist_metadata so media_playlist_membership rows can resolve a playlist's title without joining playlists.playlists")]
public sealed class M009_CreatePlaylistMetadataTable : Migration
{
    private const string SchemaName = "playlists";

    public override void Up()
    {
        Create.Table("playlist_metadata").InSchema(SchemaName)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("title").AsCustom("text").Nullable();

        Create.ForeignKey("fk_metadata_playlist_metadata_playlist_id")
            .FromTable("playlist_metadata").InSchema(SchemaName).ForeignColumn("playlist_id")
            .ToTable("playlists").InSchema(SchemaName).PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_metadata_playlist_metadata_playlist_id " +
            "ON playlists.playlist_metadata (playlist_id);");

        // Backfill from existing playlists.playlists rows so any pre-existing playlists are
        // immediately resolvable through the metadata table.
        Execute.Sql(
            "INSERT INTO playlists.playlist_metadata (playlist_id, title) " +
            "SELECT playlist_id, title FROM playlists.playlists " +
            "ON CONFLICT (playlist_id) DO NOTHING;");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS playlists.ux_metadata_playlist_metadata_playlist_id;");
        Delete.ForeignKey("fk_metadata_playlist_metadata_playlist_id")
            .OnTable("playlist_metadata").InSchema(SchemaName);
        Delete.Table("playlist_metadata").InSchema(SchemaName);
    }
}
