using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(40, TransactionBehavior.None, "Add 'ignored' download job state for keyword-suppressed videos")]
public sealed class M040_AddIgnoredDownloadJobState : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TYPE downloads.download_job_state ADD VALUE IF NOT EXISTS 'ignored';");
    }

    public override void Down()
    {
        // PostgreSQL enum labels cannot be dropped safely.
    }
}
