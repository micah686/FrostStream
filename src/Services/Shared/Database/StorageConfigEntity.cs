using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
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
    public StorageParametersBase? TypedParameters
    {
        get
        {
            return Method switch
            {
                StorageMethod.Local when Local is not null => new PosixLocalStorageParameters
                {
                    Protocol = Local.Protocol,
                    Path = Local.Path
                },
                StorageMethod.Network when Network is not null => new StreamingNetworkStorageParameters
                {
                    Protocol = Network.Protocol,
                    Host = Network.Host,
                    Port = Network.Port,
                    Username = Network.Username,
                    Password = Network.Password,
                    PrivateKey = Network.PrivateKey,
                    PublicKey = Network.PublicKey,
                    BasePath = Network.BasePath
                },
                StorageMethod.ObjectStorage when ObjectS3Compatible is not null => new S3CompatibleObjectStorageParameters
                {
                    Provider = ObjectS3Compatible.Provider,
                    BucketName = ObjectS3Compatible.BucketName,
                    Region = ObjectS3Compatible.Region,
                    Endpoint = ObjectS3Compatible.Endpoint,
                    AccessKeyId = ObjectS3Compatible.AccessKeyId,
                    SecretKeyId = ObjectS3Compatible.SecretKeyId,
                    SessionTokenSecretId = ObjectS3Compatible.SessionTokenSecretId,
                    ForcePathStyle = ObjectS3Compatible.ForcePathStyle,
                    UseSsl = ObjectS3Compatible.UseSsl
                },
                StorageMethod.ObjectStorage when ObjectAzureBlob is not null => new AzureBlobObjectStorageParameters
                {
                    CredentialMode = ObjectAzureBlob.CredentialMode,
                    ContainerName = ObjectAzureBlob.ContainerName,
                    AzureAccountName = ObjectAzureBlob.AzureAccountName,
                    AzureAccountKeySecretId = ObjectAzureBlob.AzureAccountKeySecretId,
                    AzureConnectionStringSecretId = ObjectAzureBlob.AzureConnectionStringSecretId,
                    AzureSasUrlSecretId = ObjectAzureBlob.AzureSasUrlSecretId
                },
                StorageMethod.ObjectStorage when ObjectGoogleCloudStorage is not null => new GoogleCloudStorageObjectStorageParameters
                {
                    BucketName = ObjectGoogleCloudStorage.BucketName,
                    CredentialMode = ObjectGoogleCloudStorage.CredentialMode,
                    GcpCredentialsJson = string.IsNullOrWhiteSpace(ObjectGoogleCloudStorage.GcpCredentialsJson)
                        ? null
                        : JsonDocument.Parse(ObjectGoogleCloudStorage.GcpCredentialsJson).RootElement.Clone(),
                    GcpCredentialsJsonIsBase64Encoded = ObjectGoogleCloudStorage.GcpCredentialsJsonIsBase64Encoded,
                    GcpCredentialsFilePath = ObjectGoogleCloudStorage.GcpCredentialsFilePath,
                    GcpProjectId = ObjectGoogleCloudStorage.GcpProjectId
                },
                _ => null
            };
        }
    }

    [NotMapped]
    public string Parameters
    {
        get
        {
            var typed = TypedParameters;
            if (typed is null)
            {
                return "{}";
            }

            return StorageParametersSerializer.Serialize(Method, typed);
        }
    }

    public void ApplyTypedParameters(StorageParametersBase parameters)
    {
        Local = null;
        Network = null;
        ObjectS3Compatible = null;
        ObjectAzureBlob = null;
        ObjectGoogleCloudStorage = null;

        switch (parameters)
        {
            case PosixLocalStorageParameters local:
                Method = StorageMethod.Local;
                Local = new StorageLocalConfigEntity
                {
                    Protocol = local.Protocol,
                    Path = local.Path
                };
                break;

            case StreamingNetworkStorageParameters network:
                Method = StorageMethod.Network;
                Network = new StorageNetworkConfigEntity
                {
                    Protocol = network.Protocol,
                    Host = network.Host,
                    Port = network.Port,
                    Username = network.Username,
                    Password = network.Password,
                    PrivateKey = network.PrivateKey,
                    PublicKey = network.PublicKey,
                    BasePath = network.BasePath
                };
                break;

            case S3CompatibleObjectStorageParameters @object:
                Method = StorageMethod.ObjectStorage;
                ObjectS3Compatible = new StorageS3CompatibleObjectConfigEntity
                {
                    Provider = @object.Provider,
                    BucketName = @object.BucketName,
                    Region = @object.Region,
                    Endpoint = @object.Endpoint,
                    AccessKeyId = @object.AccessKeyId,
                    SecretKeyId = @object.SecretKeyId,
                    SessionTokenSecretId = @object.SessionTokenSecretId,
                    ForcePathStyle = @object.ForcePathStyle,
                    UseSsl = @object.UseSsl
                };
                break;

            case AzureBlobObjectStorageParameters @object:
                Method = StorageMethod.ObjectStorage;
                ObjectAzureBlob = new StorageAzureBlobObjectConfigEntity
                {
                    CredentialMode = @object.CredentialMode,
                    ContainerName = @object.ContainerName,
                    AzureAccountName = @object.AzureAccountName,
                    AzureAccountKeySecretId = @object.AzureAccountKeySecretId,
                    AzureConnectionStringSecretId = @object.AzureConnectionStringSecretId,
                    AzureSasUrlSecretId = @object.AzureSasUrlSecretId
                };
                break;

            case GoogleCloudStorageObjectStorageParameters @object:
                Method = StorageMethod.ObjectStorage;
                ObjectGoogleCloudStorage = new StorageGoogleCloudStorageObjectConfigEntity
                {
                    BucketName = @object.BucketName,
                    CredentialMode = @object.CredentialMode,
                    GcpCredentialsJson = @object.GcpCredentialsJson?.GetRawText(),
                    GcpCredentialsJsonIsBase64Encoded = @object.GcpCredentialsJsonIsBase64Encoded,
                    GcpCredentialsFilePath = @object.GcpCredentialsFilePath,
                    GcpProjectId = @object.GcpProjectId
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

    public string? Password { get; set; }

    public string? PrivateKey { get; set; }

    public string? PublicKey { get; set; }

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

    [Required]
    [MinLength(1)]
    public required string AccessKeyId { get; set; }

    [Required]
    [MinLength(1)]
    public required string SecretKeyId { get; set; }

    public string? SessionTokenSecretId { get; set; }

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

    public string? AzureAccountKeySecretId { get; set; }

    public string? AzureConnectionStringSecretId { get; set; }

    public string? AzureSasUrlSecretId { get; set; }

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

    public string? GcpCredentialsJson { get; set; }

    public bool GcpCredentialsJsonIsBase64Encoded { get; set; }

    public string? GcpCredentialsFilePath { get; set; }

    public string? GcpProjectId { get; set; }

    public StorageConfigEntity StorageConfig { get; set; } = null!;
}
