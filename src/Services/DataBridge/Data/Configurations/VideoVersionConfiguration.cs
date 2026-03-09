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

        builder.HasOne(x => x.VideoInfo)
            .WithMany(x => x.Versions)
            .HasForeignKey(x => x.VideoId);
    }
}
