using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Database;

namespace DataBridge.Data;

public sealed class CreatorSourceConfiguration : IEntityTypeConfiguration<CreatorSourceEntity>
{
    public void Configure(EntityTypeBuilder<CreatorSourceEntity> builder)
    {
        builder.ToTable("creator_sources");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.Platform).HasColumnName("platform").HasMaxLength(50).IsRequired();
        builder.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(50).HasConversion<string>().IsRequired();
        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.ScanEnabled).HasColumnName("scan_enabled").IsRequired();
        builder.Property(x => x.IncrementalPageSize).HasColumnName("incremental_page_size").IsRequired();
        builder.Property(x => x.ConsecutiveKnownThreshold).HasColumnName("consecutive_known_threshold").IsRequired();
        builder.Property(x => x.FullRescanIntervalDays).HasColumnName("full_rescan_interval_days").IsRequired();
        builder.Property(x => x.MetadataRefreshWindow).HasColumnName("metadata_refresh_window").IsRequired();
        builder.Property(x => x.LastSuccessfulScanAt).HasColumnName("last_successful_scan_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastFullScanAt).HasColumnName("last_full_scan_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastSeenHighWatermark).HasColumnName("last_seen_high_watermark").HasMaxLength(512);
        builder.Property(x => x.NextFullScanStartIndex).HasColumnName("next_full_scan_start_index");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();
        builder.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamp with time zone");
        builder.Property(x => x.AvatarUrl).HasColumnName("avatar_url").HasMaxLength(4096);
        builder.Property(x => x.AvatarContentHash).HasColumnName("avatar_content_hash").HasMaxLength(64);
        builder.Property(x => x.BannerUrl).HasColumnName("banner_url").HasMaxLength(4096);
        builder.Property(x => x.BannerContentHash).HasColumnName("banner_content_hash").HasMaxLength(64);
        builder.Property(x => x.AssetsLastRefreshedAt).HasColumnName("assets_last_refreshed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.AssetsLastAttemptAt).HasColumnName("assets_last_attempt_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.AssetsAttemptCount).HasColumnName("assets_attempt_count").IsRequired();
        builder.Property(x => x.AssetsLastError).HasColumnName("assets_last_error").HasMaxLength(2048);

        builder.HasIndex(x => x.SourceUrl).HasDatabaseName("uq_creator_sources_source_url").IsUnique();
        builder.HasIndex(x => x.ScanEnabled).HasDatabaseName("ix_creator_sources_scan_enabled");
    }
}

public sealed class DiscoveredMediaConfiguration : IEntityTypeConfiguration<DiscoveredMediaEntity>
{
    public void Configure(EntityTypeBuilder<DiscoveredMediaEntity> builder)
    {
        builder.ToTable("discovered_media");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.CreatorSourceId).HasColumnName("creator_source_id").IsRequired();
        builder.Property(x => x.Platform).HasColumnName("platform").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Extractor).HasColumnName("extractor").HasMaxLength(255).IsRequired();
        builder.Property(x => x.ExternalMediaId).HasColumnName("external_media_id").HasMaxLength(512).IsRequired();
        builder.Property(x => x.CanonicalUrl).HasColumnName("canonical_url").HasMaxLength(4096).IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(2048);
        builder.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
        builder.Property(x => x.ThumbnailUrl).HasColumnName("thumbnail_url").HasMaxLength(4096);
        builder.Property(x => x.LiveStatus).HasColumnName("live_status").HasMaxLength(100);
        builder.Property(x => x.Availability).HasColumnName("availability").HasMaxLength(100);
        builder.Property(x => x.DiscoveryStatus).HasColumnName("discovery_status").HasMaxLength(50).HasConversion<string>().IsRequired();
        builder.Property(x => x.MetadataStatus).HasColumnName("metadata_status").HasMaxLength(50).HasConversion<string>().IsRequired();
        builder.Property(x => x.FirstSeenAt)
            .HasColumnName("first_seen_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.MissedFullScanCount).HasColumnName("missed_full_scan_count").IsRequired();
        builder.Property(x => x.LastChangedAt).HasColumnName("last_changed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastEnqueuedAt).HasColumnName("last_enqueued_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastUpdated).HasColumnName("last_updated").HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new { x.Platform, x.Extractor, x.ExternalMediaId })
            .HasDatabaseName("ux_discovered_media_identity")
            .IsUnique();
        builder.HasIndex(x => x.CreatorSourceId).HasDatabaseName("ix_discovered_media_creator_source_id");
        builder.HasIndex(x => x.MetadataStatus).HasDatabaseName("ix_discovered_media_metadata_status");

        builder.HasOne<CreatorSourceEntity>()
            .WithMany()
            .HasForeignKey(x => x.CreatorSourceId)
            .HasConstraintName("fk_discovered_media_creator_source_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
