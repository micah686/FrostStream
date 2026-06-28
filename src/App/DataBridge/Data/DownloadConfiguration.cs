using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class DownloadJobConfiguration : IEntityTypeConfiguration<DownloadJobEntity>
{
    public void Configure(EntityTypeBuilder<DownloadJobEntity> builder)
    {
        builder.ToTable("download_jobs", "downloads");

        builder.HasKey(x => x.JobId);

        builder.Property(x => x.JobId).HasColumnName("job_id").ValueGeneratedNever();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();

        builder.Property(x => x.State)
            .HasColumnName("state")
            .HasColumnType("downloads.download_job_state")
            .IsRequired();

        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(255);
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100);

        builder.Property(x => x.AttemptMetadata).HasColumnName("attempt_metadata").IsRequired();
        builder.Property(x => x.AttemptDownload).HasColumnName("attempt_download").IsRequired();
        builder.Property(x => x.AttemptUpload).HasColumnName("attempt_upload").IsRequired();

        builder.Property(x => x.TempFileRef).HasColumnName("temp_file_ref").HasMaxLength(2048);
        builder.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
        builder.Property(x => x.ContentHashXxh128).HasColumnName("content_hash_xxh128").HasMaxLength(64);
        builder.Property(x => x.StorageVersion).HasColumnName("storage_version").HasMaxLength(255);
        builder.Property(x => x.InfoJsonStoragePath).HasColumnName("info_json_storage_path").HasMaxLength(2048);
        builder.Property(x => x.InfoJsonContentHashXxh128).HasColumnName("info_json_content_hash_xxh128").HasMaxLength(64);
        builder.Property(x => x.InfoJsonSizeBytes).HasColumnName("info_json_size_bytes");
        builder.Property(x => x.MetaStoragePath).HasColumnName("meta_storage_path").HasMaxLength(2048);
        builder.Property(x => x.Priority).HasColumnName("priority").HasDefaultValue(0).IsRequired();
        builder.Property(x => x.IngestOrigin)
            .HasColumnName("ingest_origin")
            .HasColumnType("media.ingest_origin")
            .HasDefaultValue(IngestOrigin.Download)
            .IsRequired();

        builder.Property(x => x.FailureKind)
            .HasColumnName("failure_kind")
            .HasColumnType("downloads.failure_kind");

        builder.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(255);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(4096);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(x => x.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.State, x.UpdatedAt })
            .HasDatabaseName("ix_download_jobs_state_updated_at");

        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_download_jobs_correlation_id");
    }
}

public sealed class DownloadJobHistoryConfiguration : IEntityTypeConfiguration<DownloadJobHistoryEntity>
{
    public void Configure(EntityTypeBuilder<DownloadJobHistoryEntity> builder)
    {
        builder.ToTable("download_job_history", "downloads");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.JobId).HasColumnName("job_id").IsRequired();
        builder.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(x => x.OperationKey).HasColumnName("operation_key").HasMaxLength(512).IsRequired();
        builder.Property(x => x.EventName).HasColumnName("event_name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");

        builder.Property(x => x.RecordedAt)
            .HasColumnName("recorded_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.HasIndex(x => new { x.JobId, x.RecordedAt })
            .HasDatabaseName("ix_download_job_history_job_id_recorded_at");

        builder.HasOne<DownloadJobEntity>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .HasConstraintName("fk_download_job_history_job_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FailedDownloadJobConfiguration : IEntityTypeConfiguration<FailedDownloadJobEntity>
{
    public void Configure(EntityTypeBuilder<FailedDownloadJobEntity> builder)
    {
        builder.ToTable("failed_download_jobs", "downloads");

        builder.HasKey(x => x.JobId);

        builder.Property(x => x.JobId).HasColumnName("job_id").ValueGeneratedNever();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();

        builder.Property(x => x.FailedState)
            .HasColumnName("failed_state")
            .HasColumnType("downloads.download_job_state")
            .IsRequired();

        builder.Property(x => x.FailureKind)
            .HasColumnName("failure_kind")
            .HasColumnType("downloads.failure_kind")
            .IsRequired();

        builder.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(255);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.LastPayloadJson).HasColumnName("last_payload_json").HasColumnType("jsonb");

        builder.Property(x => x.FailedAt)
            .HasColumnName("failed_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();
    }
}

public sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessageEntity>
{
    public void Configure(EntityTypeBuilder<ProcessedMessageEntity> builder)
    {
        builder.ToTable("processed_messages", "downloads");

        builder.HasKey(x => x.MessageId);

        builder.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(x => x.OperationKey).HasColumnName("operation_key").HasMaxLength(512).IsRequired();
        builder.Property(x => x.JobId).HasColumnName("job_id").IsRequired();

        builder.Property(x => x.ProcessedAt)
            .HasColumnName("processed_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.HasIndex(x => x.JobId).HasDatabaseName("ix_processed_messages_job_id");
        builder.HasIndex(x => x.OperationKey).HasDatabaseName("ix_processed_messages_operation_key");
    }
}

public sealed class MediaConfiguration : IEntityTypeConfiguration<MediaEntity>
{
    public void Configure(EntityTypeBuilder<MediaEntity> builder)
    {
        builder.ToTable("media", "media");

        builder.HasKey(x => x.MediaGuid);

        builder.Property(x => x.MediaGuid).HasColumnName("media_guid").ValueGeneratedNever();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();
    }
}

public sealed class MediaSourceVersionConfiguration : IEntityTypeConfiguration<MediaSourceVersionEntity>
{
    public void Configure(EntityTypeBuilder<MediaSourceVersionEntity> builder)
    {
        builder.ToTable("media_source_versions", "media");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(255);
        builder.Property(x => x.SourceMediaId).HasColumnName("source_media_id").HasMaxLength(512);
        builder.Property(x => x.SourceLastModified)
            .HasColumnName("source_last_modified")
            .HasColumnType("timestamp with time zone");
        builder.Property(x => x.MediaGuid).HasColumnName("media_guid").IsRequired();
        builder.Property(x => x.LatestJobId).HasColumnName("latest_job_id");

        builder.HasIndex(x => new { x.Provider, x.SourceMediaId })
            .HasDatabaseName("ix_media_source_versions_provider_source_media_id");

        builder.HasIndex(x => x.MediaGuid)
            .HasDatabaseName("ix_media_source_versions_media_guid");

        builder.HasOne<DownloadJobEntity>()
            .WithMany()
            .HasForeignKey(x => x.LatestJobId)
            .HasConstraintName("fk_media_source_versions_latest_job_id")
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class MediaContentIdVersionConfiguration : IEntityTypeConfiguration<MediaContentIdVersionEntity>
{
    public void Configure(EntityTypeBuilder<MediaContentIdVersionEntity> builder)
    {
        builder.ToTable("media_content_id_versions", "media");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.MediaGuid).HasColumnName("media_guid").IsRequired();
        builder.Property(x => x.ContentHashXxh128)
            .HasColumnName("content_hash_xxh128")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.VersionNum).HasColumnName("version_num").IsRequired();
        builder.Property(x => x.IngestOrigin)
            .HasColumnName("ingest_origin")
            .HasColumnType("media.ingest_origin")
            .HasDefaultValue(IngestOrigin.Download)
            .IsRequired();

        builder.HasIndex(x => new { x.MediaGuid, x.VersionNum })
            .IsUnique()
            .HasDatabaseName("ux_media_content_id_versions_media_guid_version_num");

        builder.HasIndex(x => new { x.StorageKey, x.ContentHashXxh128 })
            .IsUnique()
            .HasDatabaseName("ux_media_content_id_versions_storage_key_content_hash");
    }
}

public sealed class AudioRenditionConfiguration : IEntityTypeConfiguration<AudioRenditionEntity>
{
    public void Configure(EntityTypeBuilder<AudioRenditionEntity> builder)
    {
        builder.ToTable("audio_renditions", "media");

        builder.HasKey(x => x.RenditionId);

        builder.Property(x => x.RenditionId).HasColumnName("rendition_id").ValueGeneratedNever();
        builder.Property(x => x.MediaGuid).HasColumnName("media_guid").IsRequired();
        builder.Property(x => x.SourceVersionNum).HasColumnName("source_version_num").IsRequired();
        builder.Property(x => x.Format)
            .HasColumnName("format")
            .HasColumnType("media.audio_rendition_format")
            .IsRequired();
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("media.audio_rendition_status")
            .IsRequired();
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(2048);
        builder.Property(x => x.ContentHashXxh128).HasColumnName("content_hash_xxh128").HasMaxLength(64);
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        builder.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
        builder.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(4096);
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();
        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(x => new { x.MediaGuid, x.SourceVersionNum, x.Format, x.StorageKey })
            .IsUnique()
            .HasDatabaseName("ux_audio_renditions_media_version_format_storage");

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ix_audio_renditions_status");

        builder.HasOne<MediaContentIdVersionEntity>()
            .WithMany()
            .HasPrincipalKey(x => new { x.MediaGuid, x.VersionNum })
            .HasForeignKey(x => new { x.MediaGuid, x.SourceVersionNum })
            .HasConstraintName("fk_audio_renditions_source_version")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
