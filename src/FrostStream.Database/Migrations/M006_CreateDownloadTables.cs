using FluentMigrator;

namespace FrostStream.Database.Migrations;

[Migration(6, "Create download jobs and video files tables")]
public class M006_CreateDownloadTables : Migration
{
    public override void Up()
    {
        // Download jobs table
        Create.Table("download_jobs")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("requested_format_ids").AsCustom("TEXT[]").Nullable()
            .WithColumn("output_format").AsString(20).Nullable()
            .WithColumn("output_quality").AsString(50).Nullable()
            .WithColumn("status").AsString(50).NotNullable().WithDefaultValue("pending")
            .WithColumn("progress_percent").AsInt32().Nullable()
            .WithColumn("bytes_downloaded").AsInt64().WithDefaultValue(0)
            .WithColumn("bytes_total").AsInt64().Nullable()
            .WithColumn("download_speed").AsInt64().Nullable()
            .WithColumn("eta_seconds").AsInt32().Nullable()
            .WithColumn("retry_count").AsInt32().WithDefaultValue(0)
            .WithColumn("max_retries").AsInt32().WithDefaultValue(3)
            .WithColumn("queued_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("started_at").AsDateTimeOffset().Nullable()
            .WithColumn("completed_at").AsDateTimeOffset().Nullable()
            .WithColumn("failed_at").AsDateTimeOffset().Nullable()
            .WithColumn("error_message").AsString(int.MaxValue).Nullable()
            .WithColumn("error_code").AsString(100).Nullable()
            .WithColumn("worker_id").AsString(100).Nullable()
            .WithColumn("source_data").AsCustom("JSONB").Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Execute.Sql("CREATE INDEX idx_jobs_status ON download_jobs(status);");
        Execute.Sql("CREATE INDEX idx_jobs_video ON download_jobs(video_id);");
        Execute.Sql("CREATE INDEX idx_jobs_queued ON download_jobs(queued_at) WHERE status = 'pending';");
        Execute.Sql("CREATE INDEX idx_jobs_worker ON download_jobs(worker_id, status);");

        // Video files table
        Create.Table("video_files")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("video_id").AsGuid().NotNullable().ForeignKey("videos", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("job_id").AsGuid().Nullable().ForeignKey("download_jobs", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("storage_backend").AsString(50).WithDefaultValue("local")
            .WithColumn("storage_path").AsString().NotNullable()
            .WithColumn("filename").AsString(500).Nullable()
            .WithColumn("format_id").AsString(100).Nullable()
            .WithColumn("ext").AsString(20).Nullable()
            .WithColumn("mime_type").AsString(100).Nullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("checksum_md5").AsString(32).Nullable()
            .WithColumn("checksum_sha256").AsString(64).Nullable()
            .WithColumn("duration").AsDecimal(10, 3).Nullable()
            .WithColumn("bitrate").AsInt64().Nullable()
            .WithColumn("video_codec").AsString(100).Nullable()
            .WithColumn("audio_codec").AsString(100).Nullable()
            .WithColumn("width").AsInt32().Nullable()
            .WithColumn("height").AsInt32().Nullable()
            .WithColumn("fps").AsDecimal(5, 2).Nullable()
            .WithColumn("status").AsString(50).WithDefaultValue("active")
            .WithColumn("archived_at").AsDateTimeOffset().Nullable()
            .WithColumn("archive_path").AsString().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Execute.Sql("CREATE INDEX idx_files_video ON video_files(video_id);");
        Execute.Sql("CREATE INDEX idx_files_status ON video_files(status);");
        Execute.Sql("CREATE INDEX idx_files_storage ON video_files(storage_backend, storage_path);");
    }

    public override void Down()
    {
        Delete.Table("video_files");
        Delete.Table("download_jobs");
    }
}
