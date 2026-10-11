using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Lets a download config set pin its jobs to a specific tagged worker pool, independent of which
/// storage backend they land on. When set, it overrides the storage key's own worker tag
/// (<c>storage.storage_keys.worker_tag</c>, added by migration 028) for jobs built from that config
/// set. Also threads the same column onto <c>jobs.playlists</c> so the persisted playlist row (used
/// to retry a single item later) doesn't silently drop the tag between submission and retry.
/// </summary>
[Migration(88, "Add worker_tag to download config sets and playlists")]
public sealed class M088_AddWorkerTagToDownloadConfigSets : Migration
{
    private const string TagPattern = "^[a-z0-9-]{2,50}$";

    public override void Up()
    {
        Alter.Table("download_config_sets").InSchema("downloads")
            .AddColumn("worker_tag").AsString(50).Nullable();
        Execute.Sql(
            "ALTER TABLE downloads.download_config_sets ADD CONSTRAINT ck_download_config_sets_worker_tag_format " +
            $"CHECK (worker_tag IS NULL OR worker_tag ~ '{TagPattern}');");

        Alter.Table("playlists").InSchema("jobs")
            .AddColumn("worker_tag").AsString(50).Nullable();
        Execute.Sql(
            "ALTER TABLE jobs.playlists ADD CONSTRAINT ck_playlists_worker_tag_format " +
            $"CHECK (worker_tag IS NULL OR worker_tag ~ '{TagPattern}');");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE jobs.playlists DROP CONSTRAINT IF EXISTS ck_playlists_worker_tag_format;");
        Delete.Column("worker_tag").FromTable("playlists").InSchema("jobs");

        Execute.Sql("ALTER TABLE downloads.download_config_sets DROP CONSTRAINT IF EXISTS ck_download_config_sets_worker_tag_format;");
        Delete.Column("worker_tag").FromTable("download_config_sets").InSchema("downloads");
    }
}
