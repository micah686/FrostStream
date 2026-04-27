using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(3, "Create download orchestration tables: download_jobs, download_job_history, failed_download_jobs, processed_messages")]
public sealed class M003_CreateDownloadJobsTables : Migration
{
    public override void Up()
    {
        Execute.Sql(
            "CREATE TYPE download_job_state AS ENUM (" +
            "'queued','metadata_pending','metadata_resolved','download_pending','downloaded_temp'," +
            "'upload_pending','uploaded','commit_pending','completed','compensating'," +
            "'failed_transient','failed_permanent','dead_lettered');");

        Execute.Sql(
            "CREATE TYPE failure_kind AS ENUM (" +
            "'unknown','transient','permanent','timeout','cancelled');");

        Create.Table("download_jobs")
            .WithColumn("job_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("correlation_id").AsCustom("uuid").NotNullable()
            .WithColumn("state").AsCustom("download_job_state").NotNullable()
            .WithColumn("source_url").AsString(4096).NotNullable()
            .WithColumn("requested_by").AsString(255).Nullable()
            .WithColumn("storage_key").AsString(100).Nullable()
            .WithColumn("archive_key").AsString(512).Nullable()
            .WithColumn("attempt_metadata").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("attempt_download").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("attempt_upload").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("temp_file_ref").AsString(2048).Nullable()
            .WithColumn("file_name").AsString(1024).Nullable()
            .WithColumn("file_size_bytes").AsInt64().Nullable()
            .WithColumn("content_hash_xxh128").AsString(64).Nullable()
            .WithColumn("content_type").AsString(255).Nullable()
            .WithColumn("object_key").AsString(2048).Nullable()
            .WithColumn("storage_version").AsString(255).Nullable()
            .WithColumn("failure_kind").AsCustom("failure_kind").Nullable()
            .WithColumn("failure_code").AsString(255).Nullable()
            .WithColumn("failure_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").Nullable();

        Create.Index("ix_download_jobs_state_updated_at")
            .OnTable("download_jobs")
            .OnColumn("state").Ascending()
            .OnColumn("updated_at").Ascending();

        Create.Index("ix_download_jobs_correlation_id")
            .OnTable("download_jobs")
            .OnColumn("correlation_id").Ascending();

        Create.Index("ix_download_jobs_archive_key")
            .OnTable("download_jobs")
            .OnColumn("archive_key").Ascending();

        Create.Table("download_job_history")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("message_id").AsCustom("uuid").NotNullable()
            .WithColumn("operation_key").AsString(512).NotNullable()
            .WithColumn("event_name").AsString(255).NotNullable()
            .WithColumn("payload_json").AsCustom("jsonb").Nullable()
            .WithColumn("recorded_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.ForeignKey("fk_download_job_history_job_id")
            .FromTable("download_job_history").ForeignColumn("job_id")
            .ToTable("download_jobs").PrimaryColumn("job_id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_download_job_history_job_id_recorded_at")
            .OnTable("download_job_history")
            .OnColumn("job_id").Ascending()
            .OnColumn("recorded_at").Ascending();

        Create.Table("failed_download_jobs")
            .WithColumn("job_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("correlation_id").AsCustom("uuid").NotNullable()
            .WithColumn("failed_state").AsCustom("download_job_state").NotNullable()
            .WithColumn("failure_kind").AsCustom("failure_kind").NotNullable()
            .WithColumn("failure_code").AsString(255).Nullable()
            .WithColumn("failure_message").AsString(4096).NotNullable()
            .WithColumn("last_payload_json").AsCustom("jsonb").Nullable()
            .WithColumn("failed_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Table("processed_messages")
            .WithColumn("message_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("operation_key").AsString(512).NotNullable()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("processed_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_processed_messages_job_id")
            .OnTable("processed_messages")
            .OnColumn("job_id").Ascending();

        Create.Index("ix_processed_messages_operation_key")
            .OnTable("processed_messages")
            .OnColumn("operation_key").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_processed_messages_operation_key").OnTable("processed_messages");
        Delete.Index("ix_processed_messages_job_id").OnTable("processed_messages");
        Delete.Table("processed_messages");

        Delete.Table("failed_download_jobs");

        Delete.Index("ix_download_job_history_job_id_recorded_at").OnTable("download_job_history");
        Delete.ForeignKey("fk_download_job_history_job_id").OnTable("download_job_history");
        Delete.Table("download_job_history");

        Delete.Index("ix_download_jobs_archive_key").OnTable("download_jobs");
        Delete.Index("ix_download_jobs_correlation_id").OnTable("download_jobs");
        Delete.Index("ix_download_jobs_state_updated_at").OnTable("download_jobs");
        Delete.Table("download_jobs");

        Execute.Sql("DROP TYPE IF EXISTS failure_kind;");
        Execute.Sql("DROP TYPE IF EXISTS download_job_state;");
    }
}
