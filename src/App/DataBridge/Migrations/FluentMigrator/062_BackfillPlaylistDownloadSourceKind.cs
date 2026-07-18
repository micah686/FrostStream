using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(62, "Mark existing playlist item jobs as playlist downloads")]
public sealed class M062_BackfillPlaylistDownloadSourceKind : Migration
{
    public override void Up()
    {
        Execute.Sql(
            """
            UPDATE downloads.download_jobs AS jobs
            SET source_kind = 1
            FROM playlists.playlist_items AS items
            WHERE items.job_id = jobs.job_id
              AND jobs.source_kind = 0;
            """);
    }

    public override void Down()
    {
        // Source attribution is corrective data and should not be made inaccurate on rollback.
    }
}
