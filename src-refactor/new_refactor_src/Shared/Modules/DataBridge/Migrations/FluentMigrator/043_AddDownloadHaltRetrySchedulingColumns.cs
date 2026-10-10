using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(43, "Add provider-halt retry scheduling columns to download jobs")]
public sealed class M043_AddDownloadHaltRetrySchedulingColumns : Migration
{
    public override void Up()
    {
        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("source_kind").AsInt32().NotNullable().WithDefaultValue(0);

        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("provider_halt_retry_at").AsCustom("timestamp with time zone").Nullable();

        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("provider_halt_retry_dispatched_at").AsCustom("timestamp with time zone").Nullable();
    }

    public override void Down()
    {
        Delete.Column("provider_halt_retry_dispatched_at").FromTable("download_jobs").InSchema("downloads");
        Delete.Column("provider_halt_retry_at").FromTable("download_jobs").InSchema("downloads");
        Delete.Column("source_kind").FromTable("download_jobs").InSchema("downloads");
    }
}
