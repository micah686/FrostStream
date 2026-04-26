using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared;
using Shared.Database;

namespace DataBridge.Data;

public sealed class StorageConfiguration : IEntityTypeConfiguration<StorageConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageConfigEntity> builder)
    {
        builder.ToTable(
            "storage_keys",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_storage_keys_key_format",
                    "\"key\" ~ '^[a-z0-9-]{2,100}$'");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.Key)
            .HasColumnName("key")
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(x => x.Key)
            .IsUnique()
            .HasDatabaseName("uq_storage_keys_key");

        builder.Property(x => x.Method)
            .HasColumnName("method")
            .HasMaxLength(50)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("description")
            .HasMaxLength(500);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAdd()
            .IsRequired();

        builder.Property(x => x.LastUpdated)
            .HasColumnName("last_updated")
            .HasColumnType("timestamp with time zone");
    }
}

public sealed class StorageLocalConfiguration : IEntityTypeConfiguration<StorageLocalConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageLocalConfigEntity> builder)
    {
        builder.ToTable("storage_keys_local");
        builder.HasKey(x => x.StorageKeyId);
        builder.Property(x => x.StorageKeyId).HasColumnName("storage_key_id").ValueGeneratedNever();
        builder.Property(x => x.Protocol).HasColumnName("protocol").HasConversion<int>().IsRequired();
        builder.Property(x => x.Path).HasColumnName("path").HasMaxLength(2048).IsRequired();
        builder.HasOne(x => x.StorageConfig)
            .WithOne(x => x.Local)
            .HasForeignKey<StorageLocalConfigEntity>(x => x.StorageKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StorageNetworkConfiguration : IEntityTypeConfiguration<StorageNetworkConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageNetworkConfigEntity> builder)
    {
        builder.ToTable("storage_keys_network");
        builder.HasKey(x => x.StorageKeyId);
        builder.Property(x => x.StorageKeyId).HasColumnName("storage_key_id").ValueGeneratedNever();
        builder.Property(x => x.Protocol).HasColumnName("protocol").HasConversion<int>().IsRequired();
        builder.Property(x => x.Host).HasColumnName("host").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Port).HasColumnName("port");
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(255);
        builder.Property(x => x.Password).HasColumnName("password").HasMaxLength(2048);
        builder.Property(x => x.PrivateKey).HasColumnName("private_key").HasMaxLength(8192);
        builder.Property(x => x.PublicKey).HasColumnName("public_key").HasMaxLength(8192);
        builder.Property(x => x.BasePath).HasColumnName("base_path").HasMaxLength(2048);
        builder.HasOne(x => x.StorageConfig)
            .WithOne(x => x.Network)
            .HasForeignKey<StorageNetworkConfigEntity>(x => x.StorageKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StorageObjectConfiguration : IEntityTypeConfiguration<StorageObjectConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageObjectConfigEntity> builder)
    {
        builder.ToTable("storage_keys_object");
        builder.HasKey(x => x.StorageKeyId);
        builder.Property(x => x.StorageKeyId).HasColumnName("storage_key_id").ValueGeneratedNever();
        builder.Property(x => x.Provider).HasColumnName("provider").HasConversion<int>().IsRequired();
        builder.Property(x => x.Container).HasColumnName("container").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Region).HasColumnName("region").HasMaxLength(255);
        builder.Property(x => x.Endpoint).HasColumnName("endpoint").HasMaxLength(2048);
        builder.Property(x => x.BasePath).HasColumnName("base_path").HasMaxLength(2048);
        builder.Property(x => x.AccessKeyId).HasColumnName("access_key_id").HasMaxLength(512);
        builder.Property(x => x.SecretKey).HasColumnName("secret_key").HasMaxLength(2048);
        builder.Property(x => x.UseDefaultCredentials).HasColumnName("use_default_credentials").IsRequired();
        builder.HasOne(x => x.StorageConfig)
            .WithOne(x => x.Object)
            .HasForeignKey<StorageObjectConfigEntity>(x => x.StorageKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
