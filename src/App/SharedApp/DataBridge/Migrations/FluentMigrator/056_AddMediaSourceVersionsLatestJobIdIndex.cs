using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// The Jobs page's "Watch" button resolves a completed job to its media item by querying
/// media_source_versions.latest_job_id, which had no index and would otherwise full-scan the table.
/// </summary>
[Migration(56, "Add index on media_source_versions.latest_job_id")]
public sealed class M056_AddMediaSourceVersionsLatestJobIdIndex : Migration
{
    public override void Up()
    {
        Create.Index("ix_media_source_versions_latest_job_id")
            .OnTable("media_source_versions").InSchema("media")
            .OnColumn("latest_job_id").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_media_source_versions_latest_job_id")
            .OnTable("media_source_versions").InSchema("media");
    }
}
