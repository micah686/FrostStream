using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(16, "Add last_run_status column to scheduled_tasks")]
public sealed class M016_AddScheduledTaskRunStatusColumn : Migration
{
    public override void Up()
    {
        Alter.Table("scheduled_tasks").InSchema("scheduling")
            .AddColumn("last_run_status").AsString(32).Nullable();
    }

    public override void Down()
    {
        Delete.Column("last_run_status").FromTable("scheduled_tasks").InSchema("scheduling");
    }
}
