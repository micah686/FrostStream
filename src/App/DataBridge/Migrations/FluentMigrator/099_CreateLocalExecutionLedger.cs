using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(99, "Create Lite local execution ledger")]
public sealed class M099_CreateLocalExecutionLedger : Migration
{
    public override void Up()
    {
        Create.Table("local_execution_work").InSchema("jobs")
            .WithColumn("work_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("kind").AsString(128).NotNullable()
            .WithColumn("deduplication_key").AsString(512).NotNullable().Unique()
            .WithColumn("payload").AsCustom("jsonb").NotNullable()
            .WithColumn("status").AsString(32).NotNullable()
            .WithColumn("attempt").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("maximum_attempts").AsInt32().NotNullable().WithDefaultValue(3)
            .WithColumn("available_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("error_code").AsString(128).Nullable()
            .WithColumn("error_message").AsString(4096).Nullable();

        Create.Index("ix_local_execution_work_dispatch")
            .OnTable("local_execution_work").InSchema("jobs")
            .OnColumn("status").Ascending()
            .OnColumn("available_at").Ascending()
            .OnColumn("created_at").Ascending();

        Execute.Sql("""
            ALTER TABLE jobs.local_execution_work
            ADD CONSTRAINT ck_local_execution_work_status
            CHECK (status IN ('queued', 'running', 'retry_waiting', 'cancellation_requested', 'completed', 'cancelled', 'failed'));
            ALTER TABLE jobs.local_execution_work
            ADD CONSTRAINT ck_local_execution_work_attempts
            CHECK (attempt >= 0 AND maximum_attempts > 0);
            """);
    }

    public override void Down()
    {
        Delete.Table("local_execution_work").InSchema("jobs");
    }
}
