using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(3)]
public class AddPendingJobLinksTable : Migration
{
    public override void Up()
    {
        Create.Table("pending_job_links")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("pending_job_id").AsGuid().NotNullable().ForeignKey("jobs", "job_id")
            .WithColumn("source_job_id").AsGuid().NotNullable().ForeignKey("jobs", "job_id")
            .WithColumn("idempotency_key").AsString(255).NotNullable()
            .WithColumn("video_id").AsGuid().Nullable().ForeignKey("video_info", "id")
            .WithColumn("existing_version_id").AsGuid().Nullable().ForeignKey("video_versions", "id")
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("completed_at").AsDateTime().Nullable();

        Create.Index("ux_pending_job_links_pending_job_id")
            .OnTable("pending_job_links")
            .OnColumn("pending_job_id")
            .Unique();

        Create.Index("ix_pending_job_links_source_job_id")
            .OnTable("pending_job_links")
            .OnColumn("source_job_id");

        Create.Index("ix_pending_job_links_idempotency_key")
            .OnTable("pending_job_links")
            .OnColumn("idempotency_key");
    }

    public override void Down()
    {
        Delete.Table("pending_job_links");
    }
}
