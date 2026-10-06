using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(42, TransactionBehavior.None, "Add 'provider_halted' download job state for provider bot detection")]
public sealed class M042_AddProviderHaltedDownloadJobState : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TYPE downloads.download_job_state ADD VALUE IF NOT EXISTS 'provider_halted';");
    }

    public override void Down()
    {
        // PostgreSQL enum labels cannot be dropped safely.
    }
}
