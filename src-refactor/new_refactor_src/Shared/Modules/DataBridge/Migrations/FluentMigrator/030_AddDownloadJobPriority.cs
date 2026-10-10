using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(30, "Add priority column to download_jobs")]
public sealed class M030_AddDownloadJobPriority : Migration
{
    public override void Up()
    {
        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("priority").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Index("ix_download_jobs_priority")
            .OnTable("download_jobs").InSchema("downloads")
            .OnColumn("state").Ascending()
            .OnColumn("priority").Descending()
            .OnColumn("created_at").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_download_jobs_priority").OnTable("download_jobs").InSchema("downloads");
        Delete.Column("priority").FromTable("download_jobs").InSchema("downloads");
    }
}
