using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(2)]
public class CreateInitialVersionedSchema : Migration
{
    public override void Up()
    {
        Create.Table("jobs")
            .WithColumn("job_id").AsGuid().PrimaryKey()
            .WithColumn("url").AsString().NotNullable()
            .WithColumn("status").AsString(50).NotNullable()
            .WithColumn("error_msg").AsString().Nullable()
            .WithColumn("retry_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("options").AsCustom("jsonb").Nullable();

        Create.Table("video_info")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("video_url").AsString().NotNullable()
            .WithColumn("platform").AsString(50).NotNullable()
            .WithColumn("source_last_modified").AsDateTime().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("metadata_json").AsCustom("jsonb").Nullable()
            .WithColumn("idempotency_key").AsString(255).Nullable()
            .WithColumn("is_dirty").AsBoolean().NotNullable().WithDefaultValue(true);

        Create.Index("ix_video_info_idempotency_key")
            .OnTable("video_info")
            .OnColumn("idempotency_key");

        Create.Table("state_tracking")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("job_id").AsGuid().NotNullable().ForeignKey("jobs", "job_id")
            .WithColumn("video_id").AsGuid().Nullable().ForeignKey("video_info", "id")
            .WithColumn("idempotency_key").AsString(255).NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("storage_path").AsString().Nullable()
            .WithColumn("file_hash").AsString(64).Nullable()
            .WithColumn("updated_at").AsDateTime().NotNullable()
            .WithColumn("completed_at").AsDateTime().Nullable()
            .WithColumn("expires_at").AsDateTime().NotNullable()
            .WithColumn("retry_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("error_details").AsString().Nullable();

        Create.Index("ux_state_tracking_idempotency_key")
            .OnTable("state_tracking")
            .OnColumn("idempotency_key").Unique();
            
        Create.Index("ux_state_tracking_job_id")
            .OnTable("state_tracking")
            .OnColumn("job_id").Unique();

        Create.Table("video_versions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("video_info", "id")
            .WithColumn("idempotency_key").AsString(255).NotNullable()
            .WithColumn("file_hash").AsString(64).NotNullable()
            .WithColumn("storage_key").AsString(100).NotNullable()
            .WithColumn("storage_path").AsString().NotNullable()
            .WithColumn("version_num").AsInt32().NotNullable();

        Create.Index("ux_video_versions_idempotency_key")
            .OnTable("video_versions")
            .OnColumn("idempotency_key").Unique();
    }

    public override void Down()
    {
        Delete.Table("video_versions");
        Delete.Table("state_tracking");
        Delete.Table("video_info");
        Delete.Table("jobs");
    }
}
