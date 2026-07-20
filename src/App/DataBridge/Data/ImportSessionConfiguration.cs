using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class ImportSessionConfiguration : IEntityTypeConfiguration<ImportSessionEntity>
{
    public void Configure(EntityTypeBuilder<ImportSessionEntity> builder)
    {
        builder.ToTable("import_sessions", "imports");
        builder.HasKey(x => x.SessionId);

        builder.Property(x => x.SessionId).HasColumnName("session_id").ValueGeneratedNever();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("imports.import_session_status").IsRequired();
        builder.Property(x => x.SourceKind).HasColumnName("source_kind").HasColumnType("imports.import_session_source_kind").IsRequired();
        builder.Property(x => x.SourceRoot).HasColumnName("source_root").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.SubPath).HasColumnName("sub_path").HasMaxLength(2048);
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.WorkerTag).HasColumnName("worker_tag").HasMaxLength(128);
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(255);
        builder.Property(x => x.TotalItems).HasColumnName("total_items").IsRequired();
        builder.Property(x => x.ProbedItems).HasColumnName("probed_items").IsRequired();
        builder.Property(x => x.ReadyItems).HasColumnName("ready_items").IsRequired();
        builder.Property(x => x.IncompleteItems).HasColumnName("incomplete_items").IsRequired();
        builder.Property(x => x.ExcludedItems).HasColumnName("excluded_items").IsRequired();
        builder.Property(x => x.ApprovedItems).HasColumnName("approved_items").IsRequired();
        builder.Property(x => x.ImportedItems).HasColumnName("imported_items").IsRequired();
        builder.Property(x => x.AlreadyImportedItems).HasColumnName("already_imported_items").IsRequired();
        builder.Property(x => x.FailedItems).HasColumnName("failed_items").IsRequired();
        builder.Property(x => x.MaxParallelItems).HasColumnName("max_parallel_items").IsRequired();
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
        builder.Property(x => x.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.Status, x.UpdatedAt })
            .HasDatabaseName("ix_import_sessions_status_updated_at");
    }
}

public sealed class ImportSessionItemConfiguration : IEntityTypeConfiguration<ImportSessionItemEntity>
{
    public void Configure(EntityTypeBuilder<ImportSessionItemEntity> builder)
    {
        builder.ToTable("import_session_items", "imports");
        builder.HasKey(x => x.ItemId);

        builder.Property(x => x.ItemId).HasColumnName("item_id").ValueGeneratedNever();
        builder.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(x => x.RelativePath).HasColumnName("relative_path").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(1024).IsRequired();
        builder.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes").IsRequired();
        builder.Property(x => x.FileMtime).HasColumnName("file_mtime").HasColumnType("timestamp with time zone");
        builder.Property(x => x.SidecarsJson).HasColumnName("sidecars").HasColumnType("jsonb");
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(255);
        builder.Property(x => x.SourceMediaId).HasColumnName("source_media_id").HasMaxLength(512);
        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(4096);
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(1024);
        builder.Property(x => x.ProbeMetadataJson).HasColumnName("probe_metadata").HasColumnType("jsonb");
        builder.Property(x => x.ScanMetadataJson).HasColumnName("scan_metadata").HasColumnType("jsonb");
        builder.Property(x => x.EnrichedMetadataJson).HasColumnName("enriched_metadata").HasColumnType("jsonb");
        builder.Property(x => x.UserMetadataJson).HasColumnName("user_metadata").HasColumnType("jsonb");
        builder.Property(x => x.MetadataState).HasColumnName("metadata_state").HasColumnType("imports.import_session_item_metadata_state").IsRequired();
        builder.Property(x => x.MetadataSource).HasColumnName("metadata_source").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.MetadataFetchState).HasColumnName("metadata_fetch_state").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.MetadataFetchAttempt).HasColumnName("metadata_fetch_attempt").IsRequired();
        builder.Property(x => x.MetadataFetchMessage).HasColumnName("metadata_fetch_message").HasMaxLength(4096);
        builder.Property(x => x.Excluded).HasColumnName("excluded").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("imports.import_session_item_status").IsRequired();
        builder.Property(x => x.Attempt).HasColumnName("attempt").IsRequired();
        builder.Property(x => x.ContentHashXxh128).HasColumnName("content_hash_xxh128").HasMaxLength(64);
        builder.Property(x => x.MediaGuid).HasColumnName("media_guid");
        builder.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(2048);
        builder.Property(x => x.StorageVersion).HasColumnName("storage_version").HasMaxLength(255);
        builder.Property(x => x.MetaStoragePath).HasColumnName("meta_storage_path").HasMaxLength(2048);
        builder.Property(x => x.InfoJsonStoragePath).HasColumnName("info_json_storage_path").HasMaxLength(2048);
        builder.Property(x => x.ThumbnailStoragePath).HasColumnName("thumbnail_storage_path").HasMaxLength(2048);
        builder.Property(x => x.CaptionStoragePathsJson).HasColumnName("caption_storage_paths").HasColumnType("jsonb");
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(255);
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
        builder.Property(x => x.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.SessionId, x.RelativePath })
            .IsUnique()
            .HasDatabaseName("ux_import_session_items_session_relative_path");
        builder.HasIndex(x => new { x.SessionId, x.Status })
            .HasDatabaseName("ix_import_session_items_session_status");
        builder.HasIndex(x => new { x.SessionId, x.MetadataState })
            .HasFilter("excluded = false")
            .HasDatabaseName("ix_import_session_items_session_metadata_state");
        builder.HasIndex(x => new { x.SessionId, x.ItemId })
            .HasDatabaseName("ix_import_session_items_session_item_id");

        builder.HasOne<ImportSessionEntity>()
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .HasConstraintName("fk_import_session_items_session_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImportSessionMappingConfiguration : IEntityTypeConfiguration<ImportSessionMappingEntity>
{
    public void Configure(EntityTypeBuilder<ImportSessionMappingEntity> builder)
    {
        builder.ToTable("import_session_mappings", "imports");
        builder.HasKey(x => x.MappingId);

        builder.Property(x => x.MappingId).HasColumnName("mapping_id").ValueGeneratedNever();
        builder.Property(x => x.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(x => x.ObjectBucket).HasColumnName("object_bucket").HasMaxLength(255).IsRequired();
        builder.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(1024).IsRequired();
        builder.Property(x => x.Format).HasColumnName("format").HasMaxLength(32).IsRequired();
        builder.Property(x => x.MatchedCount).HasColumnName("matched_count").IsRequired();
        builder.Property(x => x.UnmatchedCount).HasColumnName("unmatched_count").IsRequired();
        builder.Property(x => x.AppliedAt)
            .HasColumnName("applied_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.HasOne<ImportSessionEntity>()
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .HasConstraintName("fk_import_session_mappings_session_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
