using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class FilesystemRescanFindingConfiguration : IEntityTypeConfiguration<FilesystemRescanFindingEntity>
{
    public void Configure(EntityTypeBuilder<FilesystemRescanFindingEntity> builder)
    {
        builder.ToTable("filesystem_rescan_findings", "maintenance");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.FindingType)
            .HasColumnName("finding_type")
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();
        builder.Property(x => x.MediaGuid).HasColumnName("media_guid");
        builder.Property(x => x.DetectedAt).HasColumnName("detected_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.ResolvedAt).HasColumnName("resolved_at").HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.StorageKey, x.StoragePath, x.FindingType })
            .IsUnique()
            .HasDatabaseName("uq_filesystem_rescan_findings_key_path_type");
    }
}
