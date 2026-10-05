using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(83, "Remove nightly processed_message_cleanup schedule")]
public sealed class M083_RemoveProcessedMessageCleanupSchedule : Migration
{
    public override void Up()
    {
        Execute.Sql("DELETE FROM scheduling.scheduled_tasks WHERE \"key\" = 'nightly-processed-message-cleanup';");
    }

    public override void Down()
    {
        // Intentionally no-op. The previous seed migrations can restore the schedule if needed.
    }
}
