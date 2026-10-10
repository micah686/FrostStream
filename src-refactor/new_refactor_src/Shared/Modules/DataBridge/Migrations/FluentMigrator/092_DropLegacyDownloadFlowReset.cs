using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Drops the one-shot V2 cutover bookkeeping table created by migration 063. Its only reader was
/// the legacy-flow sweep in <c>DownloadFlowStartupService</c>, which was removed together with the
/// <c>DownloadArchiveFlow</c> tombstone once every environment had completed the cutover.
/// </summary>
[Migration(92, "Drop legacy download flow reset table")]
public sealed class M092_DropLegacyDownloadFlowReset : Migration
{
    public override void Up()
    {
        Execute.Sql("DROP TABLE IF EXISTS downloads.legacy_download_flow_reset;");
    }

    public override void Down()
    {
        // Recreated empty: the pre-V2 job rows it referenced were deleted by migration 063 itself,
        // so there is nothing left to repopulate it from.
        Execute.Sql("""
            CREATE TABLE IF NOT EXISTS downloads.legacy_download_flow_reset (
              job_id uuid PRIMARY KEY,
              deleted_at timestamp with time zone NULL
            );
            """);
    }
}
