using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class StorageConfigEntityConfiguration : IEntityTypeConfiguration<StorageConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageConfigEntity> builder)
    {
        builder.ToTable("storage_configs");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(e => e.Key)
            .HasColumnName("key")
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.Method)
            .HasColumnName("method")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(e => e.ConnectionString)
            .HasColumnName("connection_string")
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(e => e.Description)
            .HasColumnName("description")
            .HasMaxLength(500);

        builder.Property(e => e.RemotePath)
            .HasColumnName("remote_path")
            .HasMaxLength(1000);

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at");

        // Unique constraint on Key
        builder.HasIndex(e => e.Key)
            .IsUnique();
    }
}
