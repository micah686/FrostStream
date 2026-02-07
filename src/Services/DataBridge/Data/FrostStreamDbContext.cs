using Microsoft.EntityFrameworkCore;
using Shared.Entities;

namespace DataBridge.Data;

public class FrostStreamDbContext : DbContext
{
    public FrostStreamDbContext(DbContextOptions<FrostStreamDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Subtitle> Subtitles => Set<Subtitle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FrostStreamDbContext).Assembly);
    }
}
