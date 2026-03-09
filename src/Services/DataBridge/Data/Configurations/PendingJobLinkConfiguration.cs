using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class PendingJobLinkConfiguration : IEntityTypeConfiguration<PendingJobLink>
{
    public void Configure(EntityTypeBuilder<PendingJobLink> builder)
    {
        builder.ToTable("pending_job_links");

        builder.HasIndex(x => x.PendingJobId).IsUnique();
        builder.HasIndex(x => x.SourceJobId);
        builder.HasIndex(x => x.IdempotencyKey);

        builder.HasOne(x => x.PendingJob)
            .WithMany()
            .HasForeignKey(x => x.PendingJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.SourceJob)
            .WithMany()
            .HasForeignKey(x => x.SourceJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.VideoInfo)
            .WithMany()
            .HasForeignKey(x => x.VideoId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.VideoVersion)
            .WithMany()
            .HasForeignKey(x => x.ExistingVersionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
