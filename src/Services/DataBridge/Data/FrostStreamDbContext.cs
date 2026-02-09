using Microsoft.EntityFrameworkCore;

namespace DataBridge.Data;

public class FrostStreamDbContext : DbContext
{
    public FrostStreamDbContext(DbContextOptions<FrostStreamDbContext> options)
        : base(options)
    {
    }

    

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FrostStreamDbContext).Assembly);
    }
}
