using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NodaTime;
using Shared.Storage;

namespace WebAPI.Controllers;

public abstract class StorageUpsertRequestBase
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}

public abstract class StorageUpdateRequestBase
{
    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class LocalStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
{
    [Required]
    public LocalStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Path { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new PosixLocalStorageParameters
        {
            Protocol = Protocol,
            Path = Path
        }).Select(error => new ValidationResult(error, [nameof(Path)]));
}

public sealed class LocalStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
{
    [Required]
    public LocalStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Path { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new PosixLocalStorageParameters
        {
            Protocol = Protocol,
            Path = Path
        }).Select(error => new ValidationResult(error, [nameof(Path)]));
}

public sealed class NetworkStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new StreamingNetworkStorageParameters
        {
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            PrivateKey = PrivateKey,
            PublicKey = PublicKey,
            BasePath = BasePath
        }).Select(error => new ValidationResult(error));
}

public sealed class NetworkStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new StreamingNetworkStorageParameters
        {
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            PrivateKey = PrivateKey,
            PublicKey = PublicKey,
            BasePath = BasePath
        }).Select(error => new ValidationResult(error));
}

public sealed class S3CompatibleObjectStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new S3CompatibleObjectStorageParameters
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
        }).Select(error => new ValidationResult(error));
}

public sealed class S3CompatibleObjectStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new S3CompatibleObjectStorageParameters
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
        }).Select(error => new ValidationResult(error));
}

public sealed class AzureBlobObjectStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
{
    [Required]
    public AzureBlobCredentialMode CredentialMode { get; init; }

    public string? ContainerName { get; init; }
    public string? AzureAccountName { get; init; }
    public string? AzureAccountKeySecretId { get; init; }
    public string? AzureConnectionStringSecretId { get; init; }
    public string? AzureSasUrlSecretId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new AzureBlobObjectStorageParameters
        {
            CredentialMode = CredentialMode,
            ContainerName = ContainerName,
            AzureAccountName = AzureAccountName,
            AzureAccountKeySecretId = AzureAccountKeySecretId,
            AzureConnectionStringSecretId = AzureConnectionStringSecretId,
            AzureSasUrlSecretId = AzureSasUrlSecretId
        }).Select(error => new ValidationResult(error));
}

public sealed class AzureBlobObjectStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
{
    [Required]
    public AzureBlobCredentialMode CredentialMode { get; init; }

    public string? ContainerName { get; init; }
    public string? AzureAccountName { get; init; }
    public string? AzureAccountKeySecretId { get; init; }
    public string? AzureConnectionStringSecretId { get; init; }
    public string? AzureSasUrlSecretId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new AzureBlobObjectStorageParameters
        {
            CredentialMode = CredentialMode,
            ContainerName = ContainerName,
            AzureAccountName = AzureAccountName,
            AzureAccountKeySecretId = AzureAccountKeySecretId,
            AzureConnectionStringSecretId = AzureConnectionStringSecretId,
            AzureSasUrlSecretId = AzureSasUrlSecretId
        }).Select(error => new ValidationResult(error));
}

public sealed class GoogleCloudStorageObjectStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = BucketName,
            CredentialMode = CredentialMode,
            GcpCredentialsJson = GcpCredentialsJson,
            GcpCredentialsJsonIsBase64Encoded = GcpCredentialsJsonIsBase64Encoded,
            GcpCredentialsFilePath = GcpCredentialsFilePath,
            GcpProjectId = GcpProjectId
        }).Select(error => new ValidationResult(error));
}

public sealed class GoogleCloudStorageObjectStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => StorageParametersSerializer.Validate(new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = BucketName,
            CredentialMode = CredentialMode,
            GcpCredentialsJson = GcpCredentialsJson,
            GcpCredentialsJsonIsBase64Encoded = GcpCredentialsJsonIsBase64Encoded,
            GcpCredentialsFilePath = GcpCredentialsFilePath,
            GcpProjectId = GcpProjectId
        }).Select(error => new ValidationResult(error));
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
