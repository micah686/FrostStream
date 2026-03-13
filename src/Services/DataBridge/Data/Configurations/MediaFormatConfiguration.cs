using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class MediaFormatConfiguration : IEntityTypeConfiguration<MediaFormat>
{
    public void Configure(EntityTypeBuilder<MediaFormat> builder)
    {
        builder.ToTable("media_formats");

        builder.HasIndex(x => x.VideoVersionId)
            .IsUnique();

        builder.HasOne(x => x.VideoVersion)
            .WithOne(x => x.MediaFormat)
            .HasForeignKey<MediaFormat>(x => x.VideoVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
