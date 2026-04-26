using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Storage;

namespace Shared.Database;

[Index(nameof(Key), IsUnique = true, Name = "uq_storage_keys_key")]
public class StorageConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; set; }

    public StorageMethod Method { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }

    public StorageLocalConfigEntity? Local { get; set; }

    public StorageNetworkConfigEntity? Network { get; set; }

    public StorageS3CompatibleObjectConfigEntity? ObjectS3Compatible { get; set; }

    public StorageAzureBlobObjectConfigEntity? ObjectAzureBlob { get; set; }

    public StorageGoogleCloudStorageObjectConfigEntity? ObjectGoogleCloudStorage { get; set; }

    [NotMapped]
    public StorageParametersStoredBase? StoredParameters
    {
        get
        {
            return Method switch
            {
                StorageMethod.Local when Local is not null => new PosixLocalStorageStored
                {
                    Protocol = Local.Protocol,
                    Path = Local.Path
                },
                StorageMethod.Network when Network is not null => new StreamingNetworkStorageStored
                {
                    Protocol = Network.Protocol,
                    Host = Network.Host,
                    Port = Network.Port,
                    Username = Network.Username,
                    BasePath = Network.BasePath
                },
                StorageMethod.ObjectStorage when ObjectS3Compatible is not null => new S3CompatibleObjectStorageStored
                {
                    Provider = ObjectS3Compatible.Provider,
                    BucketName = ObjectS3Compatible.BucketName,
                    Region = ObjectS3Compatible.Region,
                    Endpoint = ObjectS3Compatible.Endpoint,
                    HasSessionToken = ObjectS3Compatible.HasSessionToken,
                    ForcePathStyle = ObjectS3Compatible.ForcePathStyle,
                    UseSsl = ObjectS3Compatible.UseSsl
                },
                StorageMethod.ObjectStorage when ObjectAzureBlob is not null => new AzureBlobObjectStorageStored
                {
                    CredentialMode = ObjectAzureBlob.CredentialMode,
                    ContainerName = ObjectAzureBlob.ContainerName,
                    AzureAccountName = ObjectAzureBlob.AzureAccountName
                },
                StorageMethod.ObjectStorage when ObjectGoogleCloudStorage is not null => new GoogleCloudStorageObjectStorageStored
                {
                    BucketName = ObjectGoogleCloudStorage.BucketName,
                    CredentialMode = ObjectGoogleCloudStorage.CredentialMode,
                    GcpCredentialsFilePath = ObjectGoogleCloudStorage.GcpCredentialsFilePath,
                    GcpProjectId = ObjectGoogleCloudStorage.GcpProjectId
                },
                _ => null
            };
        }
    }

    public void ApplyStoredParameters(StorageParametersStoredBase parameters)
    {
        Local = null;
        Network = null;
        ObjectS3Compatible = null;
        ObjectAzureBlob = null;
        ObjectGoogleCloudStorage = null;

        switch (parameters)
        {
            case PosixLocalStorageStored local:
                Method = StorageMethod.Local;
                Local = new StorageLocalConfigEntity
                {
                    Protocol = local.Protocol,
                    Path = local.Path
                };
                break;

            case StreamingNetworkStorageStored network:
                Method = StorageMethod.Network;
                Network = new StorageNetworkConfigEntity
                {
                    Protocol = network.Protocol,
                    Host = network.Host,
                    Port = network.Port,
                    Username = network.Username,
                    BasePath = network.BasePath
                };
                break;

            case S3CompatibleObjectStorageStored s3:
                Method = StorageMethod.ObjectStorage;
                ObjectS3Compatible = new StorageS3CompatibleObjectConfigEntity
                {
                    Provider = s3.Provider,
                    BucketName = s3.BucketName,
                    Region = s3.Region,
                    Endpoint = s3.Endpoint,
                    HasSessionToken = s3.HasSessionToken,
                    ForcePathStyle = s3.ForcePathStyle,
                    UseSsl = s3.UseSsl
                };
                break;

            case AzureBlobObjectStorageStored azure:
                Method = StorageMethod.ObjectStorage;
                ObjectAzureBlob = new StorageAzureBlobObjectConfigEntity
                {
                    CredentialMode = azure.CredentialMode,
                    ContainerName = azure.ContainerName,
                    AzureAccountName = azure.AzureAccountName
                };
                break;

            case GoogleCloudStorageObjectStorageStored gcs:
                Method = StorageMethod.ObjectStorage;
                ObjectGoogleCloudStorage = new StorageGoogleCloudStorageObjectConfigEntity
                {
                    BucketName = gcs.BucketName,
                    CredentialMode = gcs.CredentialMode,
                    GcpCredentialsFilePath = gcs.GcpCredentialsFilePath,
                    GcpProjectId = gcs.GcpProjectId
                };
                break;

            default:
                throw new ArgumentException($"Unsupported parameters type: {parameters.GetType().Name}", nameof(parameters));
        }
    }
}

public sealed class StorageLocalConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int StorageKeyId { get; set; }

    public LocalStorageProtocol Protocol { get; set; }

    [Required]
    [MinLength(1)]
    public required string Path { get; set; }

    public StorageConfigEntity StorageConfig { get; set; } = null!;
}

public sealed class StorageNetworkConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int StorageKeyId { get; set; }

    public NetworkStorageProtocol Protocol { get; set; }

    [Required]
    [MinLength(1)]
    public required string Host { get; set; }

    [Range(1, 65535)]
    public int? Port { get; set; }

    public string? Username { get; set; }

    public string? BasePath { get; set; }

    public StorageConfigEntity StorageConfig { get; set; } = null!;
}

public sealed class StorageS3CompatibleObjectConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int StorageKeyId { get; set; }

    public S3CompatibleObjectStorageProvider Provider { get; set; }

    [Required]
    [MinLength(1)]
    public required string BucketName { get; set; }

    public string? Region { get; set; }

    public string? Endpoint { get; set; }

    public bool HasSessionToken { get; set; }

    public bool ForcePathStyle { get; set; }

    public bool? UseSsl { get; set; }

    public StorageConfigEntity StorageConfig { get; set; } = null!;
}

public sealed class StorageAzureBlobObjectConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int StorageKeyId { get; set; }

    public AzureBlobCredentialMode CredentialMode { get; set; }

    public string? ContainerName { get; set; }

    public string? AzureAccountName { get; set; }

    public StorageConfigEntity StorageConfig { get; set; } = null!;
}

public sealed class StorageGoogleCloudStorageObjectConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int StorageKeyId { get; set; }

    [Required]
    [MinLength(1)]
    public required string BucketName { get; set; }

    public GoogleCloudStorageCredentialMode CredentialMode { get; set; }

    public string? GcpCredentialsFilePath { get; set; }

    public string? GcpProjectId { get; set; }

    public StorageConfigEntity StorageConfig { get; set; } = null!;
}
