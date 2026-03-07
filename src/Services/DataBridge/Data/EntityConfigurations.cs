using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
    }
}

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

public class VideoInfoConfiguration : IEntityTypeConfiguration<VideoInfo>
{
    public void Configure(EntityTypeBuilder<VideoInfo> builder)
    {
        builder.ToTable("video_info");
        
        builder.HasIndex(x => x.IdempotencyKey);
    }
}

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
