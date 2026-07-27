using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Keeps user-facing playlist library data independent from playlist download-job history.
/// <c>jobs.playlists</c> and <c>jobs.playlist_source_metadata</c> describe a download request and
/// its provider refresh state. The rows in <c>playlists.playlist_metadata</c> and
/// <c>playlists.media_playlist_membership</c> describe the durable library and must not be
/// cascaded away when the job rows are purged.
/// </summary>
[Migration(80, "Detach playlist library metadata from download job history")]
public sealed class M080_DetachPlaylistLibraryMetadata : Migration
{
    public override void Up()
    {
        Delete.ForeignKey("fk_metadata_playlist_metadata_playlist_id")
            .OnTable("playlist_metadata").InSchema("playlists");
        Delete.ForeignKey("fk_media_playlist_membership_playlist_id")
            .OnTable("media_playlist_membership").InSchema("playlists");
    }

    public override void Down()
    {
        // A down migration is only valid while every library row still has a corresponding
        // job playlist row; after this migration intentionally preserves orphaned library rows,
        // PostgreSQL will reject recreating these constraints rather than deleting data.
        Create.ForeignKey("fk_metadata_playlist_metadata_playlist_id")
            .FromTable("playlist_metadata").InSchema("playlists").ForeignColumn("playlist_id")
            .ToTable("playlists").InSchema("jobs").PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);
        Create.ForeignKey("fk_media_playlist_membership_playlist_id")
            .FromTable("media_playlist_membership").InSchema("playlists").ForeignColumn("playlist_id")
            .ToTable("playlists").InSchema("jobs").PrimaryColumn("playlist_id")
            .OnDelete(Rule.Cascade);
    }
}
