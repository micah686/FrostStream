using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class JobTrackerConfiguration : IEntityTypeConfiguration<JobTracker>
{
    public void Configure(EntityTypeBuilder<JobTracker> builder)
    {
        builder.ToTable("state_tracking");

        builder.HasIndex(x => x.IdempotencyKey).IsUnique();

        builder.HasOne(x => x.Job)
            .WithOne(x => x.Tracker)
            .HasForeignKey<JobTracker>(x => x.JobId);

        builder.HasOne(x => x.VideoInfo)
            .WithMany(x => x.JobTrackers)
            .HasForeignKey(x => x.VideoId);
    }
}
