using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// yt-dlp's live per-line progress ("Destination: ...", "100.0% of 11.30MiB at 31.33MiB/s ETA 00:00",
/// "Merging formats into ...") previously only reached the client over the advisory SSE stream and was
/// never persisted, so the Jobs page log lost that detail on refresh, showing only the coarse
/// download_job_history lifecycle events. This table durably captures those lines (throttled the same
/// way the SSE hub throttles them — see ProgressForwardGate) so the two can be merged into one log.
/// </summary>
[Migration(57, "Create download_job_progress_log table")]
public sealed class M057_CreateDownloadJobProgressLog : Migration
{
    public override void Up()
    {
        Create.Table("download_job_progress_log").InSchema("downloads")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("sequence").AsInt32().NotNullable()
            .WithColumn("message").AsString(2048).NotNullable()
            .WithColumn("recorded_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_download_job_progress_log_job_id_recorded_at")
            .OnTable("download_job_progress_log").InSchema("downloads")
            .OnColumn("job_id").Ascending()
            .OnColumn("recorded_at").Ascending();

        Create.ForeignKey("fk_download_job_progress_log_job_id")
            .FromTable("download_job_progress_log").InSchema("downloads").ForeignColumn("job_id")
            .ToTable("download_jobs").InSchema("downloads").PrimaryColumn("job_id")
            .OnDelete(System.Data.Rule.Cascade);
    }

    public override void Down()
    {
        Delete.Table("download_job_progress_log").InSchema("downloads");
    }
}
