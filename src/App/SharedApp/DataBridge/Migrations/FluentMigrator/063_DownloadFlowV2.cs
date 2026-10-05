using System.Data;
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// Destructive cutover of queue/orchestration state only. Durable media, metadata, playlist
/// membership, creator discovery, configuration, schedules, storage, and import flows are retained.
/// </summary>
[Migration(63, TransactionBehavior.None, "Replace the legacy download saga state with Download Flow V2")]
public sealed class M063_DownloadFlowV2 : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TYPE downloads.download_job_status AS ENUM (
              'queued','running','stopping','stopped','compensating','completed',
              'completed_with_warnings','failed','already_downloaded','ignored');
            CREATE TYPE downloads.download_stage AS ENUM (
              'none','metadata','duplicate_check','waiting_for_worker','media_acquire',
              'primary_media_upload','meta_sidecar_upload','info_json_upload','thumbnail_upload',
              'caption_upload','rich_metadata_write','finalize','cleanup','compensation');
            CREATE TYPE downloads.download_stage_status AS ENUM (
              'pending','running','retry_waiting','succeeded','skipped','warning','failed','stopped');
            CREATE TYPE downloads.download_group_kind AS ENUM ('direct','playlist','channel','creator_monitor');
            CREATE TYPE downloads.download_group_status AS ENUM (
              'queued','expanding','running','stopping','stopped','completed',
              'completed_with_warnings','completed_with_failures','failed');
            CREATE TYPE downloads.download_artifact_status AS ENUM (
              'pending','uploading','stored','warning','failed','deleted','residual');
            CREATE TYPE downloads.download_worker_lease_status AS ENUM (
              'active','released','expired','rejected','stopped');
            ALTER TYPE downloads.failure_kind ADD VALUE IF NOT EXISTS 'interrupted';
            ALTER TYPE downloads.failure_kind ADD VALUE IF NOT EXISTS 'provider_blocked';
            ALTER TYPE downloads.failure_kind ADD VALUE IF NOT EXISTS 'stopped';
            """);

        // Capture IDs before clearing the queue so startup can delete only legacy download flows.
        Execute.Sql("""
            CREATE TABLE downloads.legacy_download_flow_reset (
              job_id uuid PRIMARY KEY,
              deleted_at timestamp with time zone NULL
            );
            INSERT INTO downloads.legacy_download_flow_reset (job_id)
              SELECT job_id FROM downloads.download_jobs
              ON CONFLICT (job_id) DO NOTHING;

            UPDATE media.media_source_versions SET latest_job_id = NULL WHERE latest_job_id IS NOT NULL;
            DELETE FROM playlists.playlist_items;
            DELETE FROM playlists.playlist_scan_entries;
            DELETE FROM downloads.failed_download_jobs;
            DELETE FROM downloads.processed_messages;
            DELETE FROM downloads.download_job_progress_log;
            DELETE FROM downloads.download_job_history;
            DELETE FROM downloads.download_jobs;
            """);

        Alter.Table("download_jobs").InSchema("downloads")
            .AddColumn("status").AsCustom("downloads.download_job_status").NotNullable().WithDefaultValue("queued")
            .AddColumn("stage").AsCustom("downloads.download_stage").NotNullable().WithDefaultValue("none")
            .AddColumn("stage_status").AsCustom("downloads.download_stage_status").NotNullable().WithDefaultValue("pending")
            .AddColumn("current_run_id").AsCustom("uuid").Nullable()
            .AddColumn("current_run_number").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("current_attempt").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("current_artifact_key").AsString(512).Nullable()
            .AddColumn("warning_count").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("stop_requested_at").AsCustom("timestamp with time zone").Nullable()
            .AddColumn("stop_requested_by").AsString(255).Nullable()
            .AddColumn("stop_reason").AsString(512).Nullable();

        Create.Index("ix_download_jobs_status_updated_at")
            .OnTable("download_jobs").InSchema("downloads")
            .OnColumn("status").Ascending()
            .OnColumn("updated_at").Ascending();

        Create.Table("download_groups").InSchema("downloads")
            .WithColumn("group_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("correlation_id").AsCustom("uuid").NotNullable()
            .WithColumn("kind").AsCustom("downloads.download_group_kind").NotNullable()
            .WithColumn("status").AsCustom("downloads.download_group_status").NotNullable()
            .WithColumn("source_url").AsString(4096).NotNullable()
            .WithColumn("requested_by").AsString(255).Nullable()
            .WithColumn("storage_key").AsString(100).Nullable()
            .WithColumn("total_jobs").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("completed_jobs").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("warning_jobs").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("failed_jobs").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("failure_code").AsString(255).Nullable()
            .WithColumn("failure_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsCustom("timestamp with time zone").Nullable();
        Create.Index("ux_download_groups_correlation_id").OnTable("download_groups").InSchema("downloads")
            .OnColumn("correlation_id").Ascending().WithOptions().Unique();

        Create.Table("download_job_runs").InSchema("downloads")
            .WithColumn("run_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("run_number").AsInt32().NotNullable()
            .WithColumn("status").AsCustom("downloads.download_job_status").NotNullable()
            .WithColumn("stage").AsCustom("downloads.download_stage").NotNullable()
            .WithColumn("stage_status").AsCustom("downloads.download_stage_status").NotNullable()
            .WithColumn("failure_kind").AsCustom("downloads.failure_kind").Nullable()
            .WithColumn("failure_code").AsString(255).Nullable()
            .WithColumn("failure_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("started_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("ended_at").AsCustom("timestamp with time zone").Nullable();
        Create.ForeignKey("fk_download_job_runs_job_id").FromTable("download_job_runs").InSchema("downloads").ForeignColumn("job_id")
            .ToTable("download_jobs").InSchema("downloads").PrimaryColumn("job_id").OnDelete(Rule.Cascade);
        Create.Index("ux_download_job_runs_job_run_number").OnTable("download_job_runs").InSchema("downloads")
            .OnColumn("job_id").Ascending().OnColumn("run_number").Ascending().WithOptions().Unique();

        Create.Table("download_stage_attempts").InSchema("downloads")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsCustom("uuid").NotNullable()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("stage").AsCustom("downloads.download_stage").NotNullable()
            .WithColumn("artifact_key").AsString(512).NotNullable().WithDefaultValue("")
            .WithColumn("attempt").AsInt32().NotNullable()
            .WithColumn("status").AsCustom("downloads.download_stage_status").NotNullable()
            .WithColumn("dispatch_id").AsCustom("uuid").NotNullable()
            .WithColumn("operation_key").AsString(512).NotNullable()
            .WithColumn("failure_kind").AsCustom("downloads.failure_kind").Nullable()
            .WithColumn("failure_code").AsString(255).Nullable()
            .WithColumn("failure_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("started_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("ended_at").AsCustom("timestamp with time zone").Nullable();
        Create.ForeignKey("fk_download_stage_attempts_run_id").FromTable("download_stage_attempts").InSchema("downloads").ForeignColumn("run_id")
            .ToTable("download_job_runs").InSchema("downloads").PrimaryColumn("run_id").OnDelete(Rule.Cascade);
        Create.Index("ux_download_stage_attempt").OnTable("download_stage_attempts").InSchema("downloads")
            .OnColumn("run_id").Ascending().OnColumn("stage").Ascending().OnColumn("artifact_key").Ascending().OnColumn("attempt").Ascending().WithOptions().Unique();
        Create.Index("ux_download_stage_attempt_dispatch").OnTable("download_stage_attempts").InSchema("downloads")
            .OnColumn("dispatch_id").Ascending().WithOptions().Unique();

        Create.Table("download_artifacts").InSchema("downloads")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsCustom("uuid").NotNullable()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("stage").AsCustom("downloads.download_stage").NotNullable()
            .WithColumn("artifact_key").AsString(512).NotNullable()
            .WithColumn("kind").AsInt32().NotNullable()
            .WithColumn("required").AsBoolean().NotNullable()
            .WithColumn("status").AsCustom("downloads.download_artifact_status").NotNullable()
            .WithColumn("temp_file_ref").AsString(2048).Nullable()
            .WithColumn("storage_key").AsString(100).Nullable()
            .WithColumn("storage_path").AsString(2048).Nullable()
            .WithColumn("storage_version").AsString(255).Nullable()
            .WithColumn("content_hash_xxh128").AsString(64).Nullable()
            .WithColumn("size_bytes").AsInt64().Nullable()
            .WithColumn("warning_code").AsString(255).Nullable()
            .WithColumn("warning_message").AsString(4096).Nullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);
        Create.ForeignKey("fk_download_artifacts_run_id").FromTable("download_artifacts").InSchema("downloads").ForeignColumn("run_id")
            .ToTable("download_job_runs").InSchema("downloads").PrimaryColumn("run_id").OnDelete(Rule.Cascade);
        Create.Index("ux_download_artifacts_run_key").OnTable("download_artifacts").InSchema("downloads")
            .OnColumn("run_id").Ascending().OnColumn("artifact_key").Ascending().WithOptions().Unique();

        Create.Table("download_worker_leases").InSchema("downloads")
            .WithColumn("dispatch_id").AsCustom("uuid").PrimaryKey()
            .WithColumn("run_id").AsCustom("uuid").NotNullable()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("stage").AsCustom("downloads.download_stage").NotNullable()
            .WithColumn("artifact_key").AsString(512).NotNullable().WithDefaultValue("")
            .WithColumn("attempt").AsInt32().NotNullable()
            .WithColumn("worker_instance_id").AsString(255).NotNullable()
            .WithColumn("status").AsCustom("downloads.download_worker_lease_status").NotNullable()
            .WithColumn("acquired_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("last_heartbeat_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("expires_at").AsCustom("timestamp with time zone").NotNullable()
            .WithColumn("released_at").AsCustom("timestamp with time zone").Nullable();
        Create.ForeignKey("fk_download_worker_leases_run_id").FromTable("download_worker_leases").InSchema("downloads").ForeignColumn("run_id")
            .ToTable("download_job_runs").InSchema("downloads").PrimaryColumn("run_id").OnDelete(Rule.Cascade);
        Create.Index("ix_download_worker_leases_status_expiry").OnTable("download_worker_leases").InSchema("downloads")
            .OnColumn("status").Ascending().OnColumn("expires_at").Ascending();

        Create.Table("download_job_warnings").InSchema("downloads")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("run_id").AsCustom("uuid").NotNullable()
            .WithColumn("job_id").AsCustom("uuid").NotNullable()
            .WithColumn("stage").AsCustom("downloads.download_stage").NotNullable()
            .WithColumn("artifact_key").AsString(512).NotNullable().WithDefaultValue("")
            .WithColumn("warning_code").AsString(255).NotNullable()
            .WithColumn("warning_message").AsString(4096).NotNullable()
            .WithColumn("created_at").AsCustom("timestamp with time zone").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);
        Create.ForeignKey("fk_download_job_warnings_run_id").FromTable("download_job_warnings").InSchema("downloads").ForeignColumn("run_id")
            .ToTable("download_job_runs").InSchema("downloads").PrimaryColumn("run_id").OnDelete(Rule.Cascade);
        Create.Index("ix_download_job_warnings_run_stage").OnTable("download_job_warnings").InSchema("downloads")
            .OnColumn("run_id").Ascending().OnColumn("stage").Ascending().OnColumn("artifact_key").Ascending();

        Create.Table("download_provider_circuits").InSchema("downloads")
            .WithColumn("provider").AsString(255).PrimaryKey()
            .WithColumn("is_open").AsBoolean().NotNullable()
            .WithColumn("reason").AsString(4096).Nullable()
            .WithColumn("opened_at").AsCustom("timestamp with time zone").Nullable()
            .WithColumn("cleared_at").AsCustom("timestamp with time zone").Nullable();
    }

    public override void Down()
    {
        Delete.Table("download_provider_circuits").InSchema("downloads");
        Delete.Table("download_job_warnings").InSchema("downloads");
        Delete.Table("download_worker_leases").InSchema("downloads");
        Delete.Table("download_artifacts").InSchema("downloads");
        Delete.Table("download_stage_attempts").InSchema("downloads");
        Delete.Table("download_job_runs").InSchema("downloads");
        Delete.Table("download_groups").InSchema("downloads");
        Delete.Index("ix_download_jobs_status_updated_at").OnTable("download_jobs").InSchema("downloads");
        Delete.Column("stop_reason").Column("stop_requested_by").Column("stop_requested_at")
            .Column("warning_count").Column("current_artifact_key").Column("current_attempt")
            .Column("current_run_number").Column("current_run_id").Column("stage_status")
            .Column("stage").Column("status").FromTable("download_jobs").InSchema("downloads");
        Delete.Table("legacy_download_flow_reset").InSchema("downloads");
        // PostgreSQL enum values/types are intentionally retained on downgrade.
    }
}
