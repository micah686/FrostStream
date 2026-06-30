using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class LocalImportBatchConfiguration : IEntityTypeConfiguration<LocalImportBatchEntity>
{
    public void Configure(EntityTypeBuilder<LocalImportBatchEntity> builder)
    {
        builder.ToTable("local_import_batches", "imports");

        builder.HasKey(x => x.BatchId);

        builder.Property(x => x.BatchId).HasColumnName("batch_id").ValueGeneratedNever();
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("imports.local_import_status")
            .IsRequired();
        builder.Property(x => x.ManifestObjectBucket).HasColumnName("manifest_object_bucket").HasMaxLength(255).IsRequired();
        builder.Property(x => x.ManifestObjectKey).HasColumnName("manifest_object_key").HasMaxLength(1024).IsRequired();
        builder.Property(x => x.SourceRoot).HasColumnName("source_root").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(255);
        builder.Property(x => x.RequestedByContext).HasColumnName("requested_by_context").HasMaxLength(255);
        builder.Property(x => x.TotalItems).HasColumnName("total_items").IsRequired();
        builder.Property(x => x.CompletedItems).HasColumnName("completed_items").IsRequired();
        builder.Property(x => x.AlreadyImportedItems).HasColumnName("already_imported_items").IsRequired();
        builder.Property(x => x.FailedItems).HasColumnName("failed_items").IsRequired();
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
            .HasDatabaseName("ix_local_import_batches_status_updated_at");
    }
}

public sealed class LocalImportItemConfiguration : IEntityTypeConfiguration<LocalImportItemEntity>
{
    public void Configure(EntityTypeBuilder<LocalImportItemEntity> builder)
    {
        builder.ToTable("local_import_items", "imports");

        builder.HasKey(x => x.ItemId);

        builder.Property(x => x.ItemId).HasColumnName("item_id").ValueGeneratedNever();
        builder.Property(x => x.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(x => x.ItemIndex).HasColumnName("item_index").IsRequired();
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("imports.local_import_status")
            .IsRequired();
        builder.Property(x => x.SourceRoot).HasColumnName("source_root").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.RelativePath).HasColumnName("relative_path").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(255);
        builder.Property(x => x.SourceMediaId).HasColumnName("source_media_id").HasMaxLength(512);
        builder.Property(x => x.SourceLastModified)
            .HasColumnName("source_last_modified")
            .HasColumnType("timestamp with time zone");
        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(4096);
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(1024);
        builder.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
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

        builder.HasIndex(x => new { x.BatchId, x.ItemIndex })
            .IsUnique()
            .HasDatabaseName("ux_local_import_items_batch_item_index");

        builder.HasIndex(x => new { x.BatchId, x.Status })
            .HasDatabaseName("ix_local_import_items_batch_status");

        builder.HasOne<LocalImportBatchEntity>()
            .WithMany()
            .HasForeignKey(x => x.BatchId)
            .HasConstraintName("fk_local_import_items_batch_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
