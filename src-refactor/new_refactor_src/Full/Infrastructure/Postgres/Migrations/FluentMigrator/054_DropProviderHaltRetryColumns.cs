using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// The +2h automatic retry for provider-halted jobs (ProviderHaltRetryService) was removed in
/// favor of the manual admin restart endpoint; these scheduling columns were its bookkeeping and
/// nothing reads them anymore.
/// </summary>
[Migration(54, "Drop unused provider-halt retry scheduling columns from download jobs")]
public sealed class M054_DropProviderHaltRetryColumns : Migration
{
    public override void Up()
    {
        Delete.Column("provider_halt_retry_dispatched_at").FromTable("download_jobs").InSchema("downloads");
        Delete.Column("provider_halt_retry_at").FromTable("download_jobs").InSchema("downloads");
    }

    public override void Down()
    {
        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("provider_halt_retry_at").AsCustom("timestamp with time zone").Nullable();

        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("provider_halt_retry_dispatched_at").AsCustom("timestamp with time zone").Nullable();
    }
}
