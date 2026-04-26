using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Storage;
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
        builder.Property(x => x.Protocol).HasColumnName("protocol").HasColumnType("local_storage_protocol").IsRequired();
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
        builder.Property(x => x.Protocol).HasColumnName("protocol").HasColumnType("network_storage_protocol").IsRequired();
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

public sealed class StorageS3CompatibleObjectConfiguration : IEntityTypeConfiguration<StorageS3CompatibleObjectConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageS3CompatibleObjectConfigEntity> builder)
    {
        builder.ToTable("storage_keys_object_s3_compatible");
        builder.HasKey(x => x.StorageKeyId);
        builder.Property(x => x.StorageKeyId).HasColumnName("storage_key_id").ValueGeneratedNever();
        builder.Property(x => x.Provider).HasColumnName("provider").HasColumnType("s3_compatible_object_storage_provider").IsRequired();
        builder.Property(x => x.BucketName).HasColumnName("bucket_name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Region).HasColumnName("region").HasMaxLength(255);
        builder.Property(x => x.Endpoint).HasColumnName("endpoint").HasMaxLength(2048);
        builder.Property(x => x.AccessKeyId).HasColumnName("access_key_id").HasMaxLength(512).IsRequired();
        builder.Property(x => x.SecretKeyId).HasColumnName("secret_key_id").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.SessionTokenSecretId).HasColumnName("session_token_secret_id").HasMaxLength(2048);
        builder.Property(x => x.ForcePathStyle).HasColumnName("force_path_style").IsRequired();
        builder.Property(x => x.UseSsl).HasColumnName("use_ssl");
        builder.HasOne(x => x.StorageConfig)
            .WithOne(x => x.ObjectS3Compatible)
            .HasForeignKey<StorageS3CompatibleObjectConfigEntity>(x => x.StorageKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StorageAzureBlobObjectConfiguration : IEntityTypeConfiguration<StorageAzureBlobObjectConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageAzureBlobObjectConfigEntity> builder)
    {
        builder.ToTable("storage_keys_object_azure_blob");
        builder.HasKey(x => x.StorageKeyId);
        builder.Property(x => x.StorageKeyId).HasColumnName("storage_key_id").ValueGeneratedNever();
        builder.Property(x => x.CredentialMode).HasColumnName("credential_mode").HasColumnType("azure_blob_credential_mode").IsRequired();
        builder.Property(x => x.ContainerName).HasColumnName("container_name").HasMaxLength(255);
        builder.Property(x => x.AzureAccountName).HasColumnName("azure_account_name").HasMaxLength(255);
        builder.Property(x => x.AzureAccountKeySecretId).HasColumnName("azure_account_key_secret_id").HasMaxLength(2048);
        builder.Property(x => x.AzureConnectionStringSecretId).HasColumnName("azure_connection_string_secret_id").HasMaxLength(4096);
        builder.Property(x => x.AzureSasUrlSecretId).HasColumnName("azure_sas_url_secret_id").HasMaxLength(4096);
        builder.HasOne(x => x.StorageConfig)
            .WithOne(x => x.ObjectAzureBlob)
            .HasForeignKey<StorageAzureBlobObjectConfigEntity>(x => x.StorageKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StorageGoogleCloudStorageObjectConfiguration : IEntityTypeConfiguration<StorageGoogleCloudStorageObjectConfigEntity>
{
    public void Configure(EntityTypeBuilder<StorageGoogleCloudStorageObjectConfigEntity> builder)
    {
        builder.ToTable("storage_keys_object_google_cloud_storage");
        builder.HasKey(x => x.StorageKeyId);
        builder.Property(x => x.StorageKeyId).HasColumnName("storage_key_id").ValueGeneratedNever();
        builder.Property(x => x.BucketName).HasColumnName("bucket_name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.CredentialMode).HasColumnName("credential_mode").HasColumnType("google_cloud_storage_credential_mode").IsRequired();
        builder.Property(x => x.GcpCredentialsJson).HasColumnName("gcp_credentials_json").HasColumnType("jsonb");
        builder.Property(x => x.GcpCredentialsJsonIsBase64Encoded).HasColumnName("gcp_credentials_json_is_base64_encoded").IsRequired();
        builder.Property(x => x.GcpCredentialsFilePath).HasColumnName("gcp_credentials_file_path").HasMaxLength(2048);
        builder.Property(x => x.GcpProjectId).HasColumnName("gcp_project_id").HasMaxLength(255);
        builder.HasOne(x => x.StorageConfig)
            .WithOne(x => x.ObjectGoogleCloudStorage)
            .HasForeignKey<StorageGoogleCloudStorageObjectConfigEntity>(x => x.StorageKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
