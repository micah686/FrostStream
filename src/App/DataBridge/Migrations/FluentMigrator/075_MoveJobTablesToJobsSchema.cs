using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Extracts all download-job tracking tables (plus the playlist download tables, which are pure
/// download-job plumbing) out of the downloads/playlists schemas into a dedicated jobs schema.
/// What remains in downloads is configuration (option presets, config sets) and the legacy
/// one-shot flow-reset bookkeeping; what remains in playlists is the media/user-facing side
/// (media_playlist_membership, playlist_metadata, user_playlists, user_playlist_items).
/// Table and constraint names are unchanged — only the schema qualifier moves, and inbound
/// foreign keys (media.media_source_versions.latest_job_id, media_playlist_membership.playlist_id)
/// follow the tables automatically.
/// </summary>
[Migration(75, "Move download job tables and playlist download tables into a jobs schema")]
public sealed class M075_MoveJobTablesToJobsSchema : Migration
{
    private const string JobsSchema = "jobs";

    private static readonly string[] DownloadsEnums =
    [
        "download_job_state",
        "download_job_status",
        "download_stage",
        "download_stage_status",
        "download_group_kind",
        "download_group_status",
        "download_artifact_status",
        "download_worker_lease_status",
        "failure_kind",
    ];

    private static readonly string[] DownloadsTables =
    [
        "download_jobs",
        "download_groups",
        "download_job_runs",
        "download_stage_attempts",
        "download_artifacts",
        "download_worker_leases",
        "download_job_warnings",
        "download_job_history",
        "download_job_progress_log",
        "failed_download_jobs",
        "processed_messages",
        "download_provider_circuits",
    ];

    private static readonly string[] PlaylistsTables =
    [
        "playlists",
        "playlist_items",
        "playlist_scan_entries",
    ];

    public override void Up()
    {
        Create.Schema(JobsSchema);

        foreach (var enumName in DownloadsEnums)
        {
            Execute.Sql($"ALTER TYPE downloads.{enumName} SET SCHEMA {JobsSchema};");
        }

        Execute.Sql($"ALTER TYPE playlists.playlist_state SET SCHEMA {JobsSchema};");

        foreach (var table in DownloadsTables)
        {
            Execute.Sql($"ALTER TABLE downloads.{table} SET SCHEMA {JobsSchema};");
        }

        foreach (var table in PlaylistsTables)
        {
            Execute.Sql($"ALTER TABLE playlists.{table} SET SCHEMA {JobsSchema};");
        }
    }

    public override void Down()
    {
        foreach (var table in PlaylistsTables)
        {
            Execute.Sql($"ALTER TABLE {JobsSchema}.{table} SET SCHEMA playlists;");
        }

        foreach (var table in DownloadsTables)
        {
            Execute.Sql($"ALTER TABLE {JobsSchema}.{table} SET SCHEMA downloads;");
        }

        Execute.Sql($"ALTER TYPE {JobsSchema}.playlist_state SET SCHEMA playlists;");

        foreach (var enumName in DownloadsEnums)
        {
            Execute.Sql($"ALTER TYPE {JobsSchema}.{enumName} SET SCHEMA downloads;");
        }

        Delete.Schema(JobsSchema);
    }
}
