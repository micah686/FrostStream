using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Splits the provider-resolved source descriptor out of jobs.playlists into a 1:1
/// jobs.playlist_source_metadata table. Those four columns describe the job's input — what the remote
/// playlist currently is, re-resolved on every scan — rather than the request or its lifecycle, which
/// is what the rest of jobs.playlists records. The media-library view of a playlist is a separate
/// concern and already lives in playlists.playlist_metadata / playlists.media_playlist_membership.
/// </summary>
[Migration(76, "Split jobs.playlists source-descriptor columns into jobs.playlist_source_metadata")]
public sealed class M076_SplitPlaylistSourceMetadata : Migration
{
    private const string SchemaName = "jobs";

    public override void Up()
    {
        Create.Table("playlist_source_metadata").InSchema(SchemaName)
            .WithColumn("playlist_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("provider_playlist_id").AsString(512).Nullable()
            .WithColumn("title").AsString(2048).Nullable()
            .WithColumn("total_items").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("last_scanned_at").AsCustom("timestamp with time zone").Nullable();

        Create.ForeignKey("fk_playlist_source_metadata_playlist_id")
            .FromTable("playlist_source_metadata").InSchema(SchemaName).ForeignColumn("playlist_id")
            .ToTable("playlists").InSchema(SchemaName).PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);

        // Every existing playlist gets its companion row so the 1:1 invariant holds from the start.
        Execute.Sql(
            "INSERT INTO jobs.playlist_source_metadata " +
            "(playlist_id, provider_playlist_id, title, total_items, last_scanned_at) " +
            "SELECT playlist_id, provider_playlist_id, title, total_items, last_scanned_at " +
            "FROM jobs.playlists;");

        Delete.Column("provider_playlist_id").FromTable("playlists").InSchema(SchemaName);
        Delete.Column("title").FromTable("playlists").InSchema(SchemaName);
        Delete.Column("total_items").FromTable("playlists").InSchema(SchemaName);
        Delete.Column("last_scanned_at").FromTable("playlists").InSchema(SchemaName);
    }

    public override void Down()
    {
        Alter.Table("playlists").InSchema(SchemaName)
            .AddColumn("provider_playlist_id").AsString(512).Nullable()
            .AddColumn("title").AsString(2048).Nullable()
            .AddColumn("total_items").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("last_scanned_at").AsCustom("timestamp with time zone").Nullable();

        Execute.Sql(
            "UPDATE jobs.playlists p SET " +
            "provider_playlist_id = m.provider_playlist_id, " +
            "title = m.title, " +
            "total_items = m.total_items, " +
            "last_scanned_at = m.last_scanned_at " +
            "FROM jobs.playlist_source_metadata m " +
            "WHERE m.playlist_id = p.playlist_id;");

        Delete.ForeignKey("fk_playlist_source_metadata_playlist_id")
            .OnTable("playlist_source_metadata").InSchema(SchemaName);
        Delete.Table("playlist_source_metadata").InSchema(SchemaName);
    }
}
