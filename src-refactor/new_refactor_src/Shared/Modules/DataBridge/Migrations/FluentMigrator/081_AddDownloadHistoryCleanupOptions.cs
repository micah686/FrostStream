using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(81, "Add download history cleanup schedule options")]
public sealed class M081_AddDownloadHistoryCleanupOptions : Migration
{
    public override void Up()
    {
        Alter.Table("scheduled_tasks").InSchema("scheduling")
            .AddColumn("retention_days").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("include_failed").AsBoolean().NotNullable().WithDefaultValue(false);

        Execute.Sql("ALTER TABLE scheduling.scheduled_tasks ADD CONSTRAINT ck_scheduled_tasks_retention_days_nonnegative CHECK (retention_days >= 0);");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE scheduling.scheduled_tasks DROP CONSTRAINT IF EXISTS ck_scheduled_tasks_retention_days_nonnegative;");
        Delete.Column("include_failed").FromTable("scheduled_tasks").InSchema("scheduling");
        Delete.Column("retention_days").FromTable("scheduled_tasks").InSchema("scheduling");
    }
}
