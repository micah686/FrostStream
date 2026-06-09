using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NodaTime;
using Shared.Storage;

namespace WebAPI.Features.Storage.Models;

public interface IStorageRequest<out TParameters>
    where TParameters : StorageParametersBase
{
    string? Description { get; }

    TParameters ToParameters();
}

public interface IStorageUpsertRequest<out TParameters> : IStorageRequest<TParameters>
    where TParameters : StorageParametersBase
{
    string Key { get; }
}

public abstract class StorageUpdateRequestBase<TParameters> : IStorageRequest<TParameters>, IValidatableObject
    where TParameters : StorageParametersBase
{
    [StringLength(500)]
    public string? Description { get; init; }

    public abstract TParameters ToParameters();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(ToParameters()).Select(error => new ValidationResult(error));
}

public class LocalStorageUpdateRequest : StorageUpdateRequestBase<PosixLocalStorageParameters>
{
    [Required]
    public LocalStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Path { get; init; }

    public override PosixLocalStorageParameters ToParameters()
        => new()
        {
            Protocol = Protocol,
            Path = Path
        };
}

public sealed class LocalStorageUpsertRequest : LocalStorageUpdateRequest, IStorageUpsertRequest<PosixLocalStorageParameters>
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }
}

public class NetworkStorageUpdateRequest : StorageUpdateRequestBase<StreamingNetworkStorageParameters>
{
    [Required]
    public NetworkStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Host { get; init; }

    [Range(1, 65535)]
    public int? Port { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? PublicKey { get; init; }
    public string? BasePath { get; init; }

    public override StreamingNetworkStorageParameters ToParameters()
        => new()
        {
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            PrivateKey = PrivateKey,
            PublicKey = PublicKey,
            BasePath = BasePath
        };
}

public sealed class NetworkStorageUpsertRequest : NetworkStorageUpdateRequest, IStorageUpsertRequest<StreamingNetworkStorageParameters>
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }
}

public class S3CompatibleObjectStorageUpdateRequest : StorageUpdateRequestBase<S3CompatibleObjectStorageParameters>
{
    [Required]
    public S3CompatibleObjectStorageProvider Provider { get; init; }

    [Required]
    [MinLength(1)]
    public required string BucketName { get; init; }

    public string? Region { get; init; }
    public string? Endpoint { get; init; }

    [Required]
    [MinLength(1)]
    public required string AccessKeyId { get; init; }

    [Required]
    [MinLength(1)]
    public required string SecretKeyId { get; init; }

    public string? SessionTokenSecretId { get; init; }
    public bool ForcePathStyle { get; init; }
    public bool? UseSsl { get; init; }

    public override S3CompatibleObjectStorageParameters ToParameters()
        => new()
        {
            Provider = Provider,
            BucketName = BucketName,
            Region = Region,
            Endpoint = Endpoint,
            AccessKeyId = AccessKeyId,
            SecretKeyId = SecretKeyId,
            SessionTokenSecretId = SessionTokenSecretId,
            ForcePathStyle = ForcePathStyle,
            UseSsl = UseSsl
        };
}

public sealed class S3CompatibleObjectStorageUpsertRequest : S3CompatibleObjectStorageUpdateRequest, IStorageUpsertRequest<S3CompatibleObjectStorageParameters>
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }
}

public class AzureBlobObjectStorageUpdateRequest : StorageUpdateRequestBase<AzureBlobObjectStorageParameters>
{
    [Required]
    public AzureBlobCredentialMode CredentialMode { get; init; }

    public string? ContainerName { get; init; }
    public string? AzureAccountName { get; init; }
    public string? AzureAccountKeySecretId { get; init; }
    public string? AzureConnectionStringSecretId { get; init; }
    public string? AzureSasUrlSecretId { get; init; }

    public override AzureBlobObjectStorageParameters ToParameters()
        => new()
        {
            CredentialMode = CredentialMode,
            ContainerName = ContainerName,
            AzureAccountName = AzureAccountName,
            AzureAccountKeySecretId = AzureAccountKeySecretId,
            AzureConnectionStringSecretId = AzureConnectionStringSecretId,
            AzureSasUrlSecretId = AzureSasUrlSecretId
        };
}

public sealed class AzureBlobObjectStorageUpsertRequest : AzureBlobObjectStorageUpdateRequest, IStorageUpsertRequest<AzureBlobObjectStorageParameters>
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }
}

public class GoogleCloudStorageObjectStorageUpdateRequest : StorageUpdateRequestBase<GoogleCloudStorageObjectStorageParameters>
{
    [Required]
    [MinLength(1)]
    public required string BucketName { get; init; }

    [Required]
    public GoogleCloudStorageCredentialMode CredentialMode { get; init; }

    public JsonElement? GcpCredentialsJson { get; init; }
    public bool GcpCredentialsJsonIsBase64Encoded { get; init; }
    public string? GcpCredentialsFilePath { get; init; }
    public string? GcpProjectId { get; init; }

    public override GoogleCloudStorageObjectStorageParameters ToParameters()
        => new()
        {
            BucketName = BucketName,
            CredentialMode = CredentialMode,
            GcpCredentialsJson = GcpCredentialsJson,
            GcpCredentialsJsonIsBase64Encoded = GcpCredentialsJsonIsBase64Encoded,
            GcpCredentialsFilePath = GcpCredentialsFilePath,
            GcpProjectId = GcpProjectId
        };
}

public sealed class GoogleCloudStorageObjectStorageUpsertRequest : GoogleCloudStorageObjectStorageUpdateRequest, IStorageUpsertRequest<GoogleCloudStorageObjectStorageParameters>
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }
}

public abstract class StorageConfigResponseBase
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public string? Description { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}

public sealed class LocalStorageConfigResponse : StorageConfigResponseBase
{
    public LocalStorageProtocol Protocol { get; init; }
    public required string Path { get; init; }
}

public sealed class NetworkStorageConfigResponse : StorageConfigResponseBase
{
    public NetworkStorageProtocol Protocol { get; init; }
    public required string Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public string? BasePath { get; init; }
}

public sealed class S3CompatibleObjectStorageConfigResponse : StorageConfigResponseBase
{
    public S3CompatibleObjectStorageProvider Provider { get; init; }
    public required string BucketName { get; init; }
    public string? Region { get; init; }
    public string? Endpoint { get; init; }
    public bool HasSessionToken { get; init; }
    public bool ForcePathStyle { get; init; }
    public bool? UseSsl { get; init; }
}

public sealed class AzureBlobObjectStorageConfigResponse : StorageConfigResponseBase
{
    public AzureBlobCredentialMode CredentialMode { get; init; }
    public string? ContainerName { get; init; }
    public string? AzureAccountName { get; init; }
}

public sealed class GoogleCloudStorageObjectStorageConfigResponse : StorageConfigResponseBase
{
    public required string BucketName { get; init; }
    public GoogleCloudStorageCredentialMode CredentialMode { get; init; }
    public string? GcpCredentialsFilePath { get; init; }
    public string? GcpProjectId { get; init; }
}
