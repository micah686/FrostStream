using Microsoft.EntityFrameworkCore;
using Shared;

namespace DataBridge.Data;

public sealed class DataBridgeDbContext(DbContextOptions<DataBridgeDbContext> options) : DbContext(options)
{
    public DbSet<StorageConfigEntity> StorageConfigs => Set<StorageConfigEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var storageConfig = modelBuilder.Entity<StorageConfigEntity>();

        storageConfig.ToTable(
            "storage_keys",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_storage_keys_key_format",
                    "\"key\" ~ '^[a-z0-9-]{2,100}$'");
            });

        storageConfig.HasKey(x => x.Id);

        storageConfig.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        storageConfig.Property(x => x.Key)
            .HasColumnName("key")
            .HasMaxLength(100)
            .IsRequired();

        storageConfig.HasIndex(x => x.Key)
            .IsUnique()
            .HasDatabaseName("uq_storage_keys_key");

        storageConfig.Property(x => x.Method)
            .HasColumnName("method")
            .HasConversion<int>()
            .IsRequired();

        storageConfig.Property(x => x.Parameters)
            .HasColumnName("parameters")
            .HasColumnType("jsonb")
            .IsRequired();

        storageConfig.Property(x => x.Description)
            .HasColumnName("description")
            .HasMaxLength(500);

        storageConfig.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        storageConfig.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at");
    }
}
