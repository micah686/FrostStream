using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(11, "Create scheduled_tasks table for the BgProcessor / Scheduler service")]
public sealed class M011_CreateScheduledTasksTable : Migration
{
    public override void Up()
    {
        Create.Table("scheduled_tasks")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("key").AsString(100).NotNullable()
            .WithColumn("task_type").AsString(100).NotNullable()
            .WithColumn("cron").AsString(255).Nullable()
            .WithColumn("interval_seconds").AsInt32().Nullable()
            .WithColumn("timezone").AsString(100).NotNullable().WithDefaultValue("UTC")
            .WithColumn("enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("catchup_policy").AsString(32).NotNullable().WithDefaultValue("Coalesce")
            .WithColumn("last_attempt_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("last_success_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("next_due_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_updated").AsCustom("timestamp with time zone").Nullable();

        Create.UniqueConstraint("uq_scheduled_tasks_key")
            .OnTable("scheduled_tasks")
            .Column("key");

        Execute.Sql(
            "ALTER TABLE scheduled_tasks ADD CONSTRAINT ck_scheduled_tasks_key_format " +
            "CHECK (\"key\" ~ '^[a-z0-9-]{2,100}$');");

        // Cron XOR interval — exactly one must be set.
        Execute.Sql(
            "ALTER TABLE scheduled_tasks ADD CONSTRAINT ck_scheduled_tasks_cron_xor_interval " +
            "CHECK ((cron IS NOT NULL) <> (interval_seconds IS NOT NULL));");

        // Seed: a disabled nightly orphan-cleanup row so operators see the proof job in
        // /api/schedules immediately and can flip it on.
        Insert.IntoTable("scheduled_tasks")
            .Row(new
            {
                id = Guid.Parse("00000000-0000-0000-0000-000000000011"),
                key = "nightly-orphan-cleanup",
                task_type = "orphan_metadata_cleanup",
                cron = "0 0 3 * * ?",
                timezone = "UTC",
                enabled = false,
                catchup_policy = "Coalesce"
            });
    }

    public override void Down()
    {
        Delete.Table("scheduled_tasks");
    }
}
