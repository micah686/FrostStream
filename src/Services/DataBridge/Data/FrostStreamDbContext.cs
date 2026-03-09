using Microsoft.EntityFrameworkCore;
using Shared.Entities;

namespace DataBridge.Data;

public class FrostStreamDbContext : DbContext
{
    public FrostStreamDbContext(DbContextOptions<FrostStreamDbContext> options)
        : base(options)
    {
    }

    public DbSet<StorageConfigEntity> StorageConfigs { get; set; }
    public DbSet<Job> Jobs { get; set; }
    public DbSet<JobTracker> JobTrackers { get; set; }
    public DbSet<VideoInfo> VideoInfos { get; set; }
    public DbSet<VideoVersion> VideoVersions { get; set; }
    public DbSet<PendingJobLink> PendingJobLinks { get; set; }
    public DbSet<DlqEntry> DlqEntries { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FrostStreamDbContext).Assembly);
    }
}
