using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared;
using Shared.Entities;

namespace DataBridge.Data.Configurations;

public class StorageConfigEntityConfiguration : IEntityTypeConfiguration<StorageConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageConfigEntity> builder)
    {
        builder.ToTable("storage_configs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Key)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(e => e.Key).IsUnique();

        builder.Property(e => e.Method)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(e => e.Parameters)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(e => e.Description);

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.UpdatedAt);
    }
}
