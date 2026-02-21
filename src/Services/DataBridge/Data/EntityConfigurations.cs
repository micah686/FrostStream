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
