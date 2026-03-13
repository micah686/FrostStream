using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class VideoVersionConfiguration : IEntityTypeConfiguration<VideoVersion>
{
    public void Configure(EntityTypeBuilder<VideoVersion> builder)
    {
        builder.ToTable("video_versions");

        builder.HasIndex(x => x.IdempotencyKey).IsUnique();

        // Index for source version lookups (for transcoded variants)
        builder.HasIndex(x => x.SourceVersionId)
            .HasDatabaseName("ix_video_versions_source_version_id");

        builder.HasOne(x => x.VideoInfo)
            .WithMany(x => x.Versions)
            .HasForeignKey(x => x.VideoId);
    }
}
