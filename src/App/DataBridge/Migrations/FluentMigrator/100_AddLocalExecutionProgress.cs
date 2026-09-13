using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(100, "Add durable Lite local execution progress")]
public sealed class M100_AddLocalExecutionProgress : Migration
{
    public override void Up()
    {
        Alter.Table("local_execution_work").InSchema("jobs")
            .AddColumn("progress_sequence").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("progress_percent").AsDouble().Nullable()
            .AddColumn("progress_message").AsString(2048).Nullable();
    }

    public override void Down()
    {
        Delete.Column("progress_message").FromTable("local_execution_work").InSchema("jobs");
        Delete.Column("progress_percent").FromTable("local_execution_work").InSchema("jobs");
        Delete.Column("progress_sequence").FromTable("local_execution_work").InSchema("jobs");
    }
}
