using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class VideoInfoConfiguration : IEntityTypeConfiguration<VideoInfo>
{
    public void Configure(EntityTypeBuilder<VideoInfo> builder)
    {
        builder.ToTable("video_info");

        builder.HasIndex(x => x.IdempotencyKey);
    }
}
