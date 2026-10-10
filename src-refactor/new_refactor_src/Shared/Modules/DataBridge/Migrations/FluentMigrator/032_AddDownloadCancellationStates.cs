using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(32, TransactionBehavior.None, "Add cancellation states to download jobs")]
public sealed class M032_AddDownloadCancellationStates : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TYPE downloads.download_job_state ADD VALUE IF NOT EXISTS 'download_queued';");
        Execute.Sql("ALTER TYPE downloads.download_job_state ADD VALUE IF NOT EXISTS 'cancelling';");
        Execute.Sql("ALTER TYPE downloads.download_job_state ADD VALUE IF NOT EXISTS 'cancelled';");
    }

    public override void Down()
    {
        // PostgreSQL enum labels cannot be dropped safely.
    }
}
