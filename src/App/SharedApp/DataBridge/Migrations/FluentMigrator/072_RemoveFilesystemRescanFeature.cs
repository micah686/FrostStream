using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Removes the filesystem rescan scheduler, reconciliation findings, and supporting database
/// objects. Historical migrations remain in place so existing migration histories stay valid.
/// </summary>
[Migration(72, "Remove filesystem rescan feature")]
public sealed class M072_RemoveFilesystemRescanFeature : Migration
{
    public override void Up()
    {
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE task_type = 'filesystem_rescan';");

        Execute.Sql("DROP TABLE IF EXISTS maintenance.filesystem_rescan_findings;");
        Execute.Sql("DROP INDEX IF EXISTS media.ix_mciv_storage_key_norm_path;");
        Execute.Sql("DROP FUNCTION IF EXISTS fs_normalize_path(text);");
    }

    public override void Down()
    {
        // The feature is intentionally not restored on rollback.
    }
}
