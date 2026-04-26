using Microsoft.EntityFrameworkCore;
using Shared.Database;
using Shared.Storage;

namespace DataBridge.Data;

public sealed class DataBridgeDbContext(DbContextOptions<DataBridgeDbContext> options) : DbContext(options)
{
    public DbSet<StorageConfigEntity> StorageConfigs => Set<StorageConfigEntity>();
    public DbSet<StorageLocalConfigEntity> StorageLocalConfigs => Set<StorageLocalConfigEntity>();
    public DbSet<StorageNetworkConfigEntity> StorageNetworkConfigs => Set<StorageNetworkConfigEntity>();
    public DbSet<StorageS3CompatibleObjectConfigEntity> StorageS3CompatibleObjectConfigs => Set<StorageS3CompatibleObjectConfigEntity>();
    public DbSet<StorageAzureBlobObjectConfigEntity> StorageAzureBlobObjectConfigs => Set<StorageAzureBlobObjectConfigEntity>();
    public DbSet<StorageGoogleCloudStorageObjectConfigEntity> StorageGoogleCloudStorageObjectConfigs => Set<StorageGoogleCloudStorageObjectConfigEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<LocalStorageProtocol>("local_storage_protocol");
        modelBuilder.HasPostgresEnum<NetworkStorageProtocol>("network_storage_protocol");
        modelBuilder.HasPostgresEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider");
        modelBuilder.HasPostgresEnum<AzureBlobCredentialMode>("azure_blob_credential_mode");
        modelBuilder.HasPostgresEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DataBridgeDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
