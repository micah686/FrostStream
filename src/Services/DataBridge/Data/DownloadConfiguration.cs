using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class DownloadJobConfiguration : IEntityTypeConfiguration<DownloadJobEntity>
{
    public void Configure(EntityTypeBuilder<DownloadJobEntity> builder)
    {
        builder.ToTable("download_jobs");

        builder.HasKey(x => x.JobId);

        builder.Property(x => x.JobId).HasColumnName("job_id").ValueGeneratedNever();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();

        builder.Property(x => x.State)
            .HasColumnName("state")
            .HasColumnType("download_job_state")
            .IsRequired();

        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(255);
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100);
        builder.Property(x => x.ArchiveKey).HasColumnName("archive_key").HasMaxLength(512);

        builder.Property(x => x.AttemptMetadata).HasColumnName("attempt_metadata").IsRequired();
        builder.Property(x => x.AttemptDownload).HasColumnName("attempt_download").IsRequired();
        builder.Property(x => x.AttemptUpload).HasColumnName("attempt_upload").IsRequired();

        builder.Property(x => x.TempFileRef).HasColumnName("temp_file_ref").HasMaxLength(2048);
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(1024);
        builder.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
        builder.Property(x => x.ContentHashXxh128).HasColumnName("content_hash_xxh128").HasMaxLength(64);
        builder.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(255);
        builder.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(2048);
        builder.Property(x => x.StorageVersion).HasColumnName("storage_version").HasMaxLength(255);

        builder.Property(x => x.FailureKind)
            .HasColumnName("failure_kind")
            .HasColumnType("failure_kind");

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

        builder.HasIndex(x => x.ArchiveKey)
            .HasDatabaseName("ix_download_jobs_archive_key");
    }
}

public sealed class DownloadJobHistoryConfiguration : IEntityTypeConfiguration<DownloadJobHistoryEntity>
{
    public void Configure(EntityTypeBuilder<DownloadJobHistoryEntity> builder)
    {
        builder.ToTable("download_job_history");

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
        builder.ToTable("failed_download_jobs");

        builder.HasKey(x => x.JobId);

        builder.Property(x => x.JobId).HasColumnName("job_id").ValueGeneratedNever();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();

        builder.Property(x => x.FailedState)
            .HasColumnName("failed_state")
            .HasColumnType("download_job_state")
            .IsRequired();

        builder.Property(x => x.FailureKind)
            .HasColumnName("failure_kind")
            .HasColumnType("failure_kind")
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
        builder.ToTable("processed_messages");

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
