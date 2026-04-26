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

    public StorageObjectConfigEntity? Object { get; set; }

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
                StorageMethod.ObjectStorage when Object is not null => new ObjectStorageParameters
                {
                    Provider = Object.Provider,
                    Container = Object.Container,
                    Region = Object.Region,
                    Endpoint = Object.Endpoint,
                    BasePath = Object.BasePath,
                    AccessKeyId = Object.AccessKeyId,
                    SecretKey = Object.SecretKey,
                    UseDefaultCredentials = Object.UseDefaultCredentials
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
        Object = null;

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

            case ObjectStorageParameters @object:
                Method = StorageMethod.ObjectStorage;
                Object = new StorageObjectConfigEntity
                {
                    Provider = @object.Provider,
                    Container = @object.Container,
                    Region = @object.Region,
                    Endpoint = @object.Endpoint,
                    BasePath = @object.BasePath,
                    AccessKeyId = @object.AccessKeyId,
                    SecretKey = @object.SecretKey,
                    UseDefaultCredentials = @object.UseDefaultCredentials
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

public sealed class StorageObjectConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int StorageKeyId { get; set; }

    public ObjectStorageProtocol Provider { get; set; }

    [Required]
    [MinLength(1)]
    public required string Container { get; set; }

    public string? Region { get; set; }

    public string? Endpoint { get; set; }

    public string? BasePath { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretKey { get; set; }

    public bool UseDefaultCredentials { get; set; } = true;

    public StorageConfigEntity StorageConfig { get; set; } = null!;
}
