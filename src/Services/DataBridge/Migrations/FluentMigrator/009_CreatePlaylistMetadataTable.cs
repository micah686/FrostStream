using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(9, "Create metadata.playlist_metadata so media_playlist_membership rows can resolve a playlist's title without joining public.playlists")]
public sealed class M009_CreatePlaylistMetadataTable : Migration
{
    private const string MetadataSchema = "metadata";

    public override void Up()
    {
        Create.Table("playlist_metadata").InSchema(MetadataSchema)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("playlist_id").AsCustom("uuid").NotNullable()
            .WithColumn("title").AsCustom("text").Nullable();

        Create.ForeignKey("fk_metadata_playlist_metadata_playlist_id")
            .FromTable("playlist_metadata").InSchema(MetadataSchema).ForeignColumn("playlist_id")
            .ToTable("playlists").PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        Execute.Sql(
            "CREATE UNIQUE INDEX ux_metadata_playlist_metadata_playlist_id " +
            "ON metadata.playlist_metadata (playlist_id);");

        // Backfill from existing public.playlists rows so any pre-existing playlists are
        // immediately resolvable through the metadata table.
        Execute.Sql(
            "INSERT INTO metadata.playlist_metadata (playlist_id, title) " +
            "SELECT playlist_id, title FROM playlists " +
            "ON CONFLICT (playlist_id) DO NOTHING;");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS metadata.ux_metadata_playlist_metadata_playlist_id;");
        Delete.ForeignKey("fk_metadata_playlist_metadata_playlist_id")
            .OnTable("playlist_metadata").InSchema(MetadataSchema);
        Delete.Table("playlist_metadata").InSchema(MetadataSchema);
    }
}
