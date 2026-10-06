using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shared.Storage;

public abstract class StorageParametersBase : IValidatableObject
{
    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return [];
    }
}

public sealed class PosixLocalStorageParameters : StorageParametersBase
{
    [Required]
    public LocalStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Path { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalStorageProtocol
{
    Local
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NetworkStorageProtocol
{
    Ftp,
    Ftps,
    Sftp,
    Nfs,
    Smb,
    Cifs
}

public sealed class StreamingNetworkStorageParameters : StorageParametersBase
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

    /// <summary>SMB/CIFS share name, without a leading server path.</summary>
    public string? ShareName { get; init; }

    /// <summary>Optional SMB authentication domain or workgroup.</summary>
    public string? Domain { get; init; }

    /// <summary>NFS export path as advertised by the server.</summary>
    public string? ExportPath { get; init; }

    /// <summary>POSIX user id presented through NFSv3 AUTH_UNIX.</summary>
    [Range(0, int.MaxValue)]
    public int? NfsUserId { get; init; }

    /// <summary>POSIX primary group id presented through NFSv3 AUTH_UNIX.</summary>
    [Range(0, int.MaxValue)]
    public int? NfsGroupId { get; init; }

    /// <summary>
    /// Absolute path where an NFS, SMB, or CIFS share has been mounted by the host or orchestrator.
    /// This remains network storage even though FluentStorage accesses the mounted filesystem via DiskStore.
    /// </summary>
    public string? MountPath { get; init; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasUsername = !string.IsNullOrWhiteSpace(Username);
        var hasPassword = !string.IsNullOrWhiteSpace(Password);
        var hasPrivateKey = !string.IsNullOrWhiteSpace(PrivateKey);

        if (!hasUsername && (hasPassword || hasPrivateKey))
        {
            yield return new ValidationResult(
                "Username is required when password or privateKey is provided.",
                [nameof(Username), nameof(Password), nameof(PrivateKey)]);
        }

        if (hasUsername && !hasPassword && !hasPrivateKey)
        {
            yield return new ValidationResult(
                "Provide either password or privateKey when username is set.",
                [nameof(Username), nameof(Password), nameof(PrivateKey)]);
        }

        if (hasPassword && hasPrivateKey)
        {
            yield return new ValidationResult(
                "Use either password-based auth or privateKey auth, not both.",
                [nameof(Password), nameof(PrivateKey)]);
        }

        var isSmb = Protocol is NetworkStorageProtocol.Smb or NetworkStorageProtocol.Cifs;
        var isNfs = Protocol == NetworkStorageProtocol.Nfs;
        var isDirectShare = isSmb || isNfs;

        if (isSmb && string.IsNullOrWhiteSpace(ShareName) && string.IsNullOrWhiteSpace(MountPath))
        {
            yield return new ValidationResult(
                $"shareName is required for {Protocol} storage when mountPath is not provided.",
                [nameof(ShareName), nameof(MountPath)]);
        }

        if (isNfs && string.IsNullOrWhiteSpace(ExportPath) && string.IsNullOrWhiteSpace(MountPath))
        {
            yield return new ValidationResult(
                "exportPath is required for Nfs storage when mountPath is not provided.",
                [nameof(ExportPath), nameof(MountPath)]);
        }

        if (!string.IsNullOrWhiteSpace(MountPath) && !Path.IsPathFullyQualified(MountPath))
        {
            yield return new ValidationResult(
                "mountPath must be an absolute filesystem path.",
                [nameof(MountPath)]);
        }

        if (!isDirectShare && !string.IsNullOrWhiteSpace(MountPath))
        {
            yield return new ValidationResult(
                "mountPath is only valid for NFS, SMB, or CIFS storage.",
                [nameof(MountPath)]);
        }

        if (!isSmb && (!string.IsNullOrWhiteSpace(ShareName) || !string.IsNullOrWhiteSpace(Domain)))
        {
            yield return new ValidationResult(
                "shareName and domain are only valid for SMB or CIFS storage.",
                [nameof(ShareName), nameof(Domain), nameof(Protocol)]);
        }

        if (!isNfs && (!string.IsNullOrWhiteSpace(ExportPath) || NfsUserId is not null || NfsGroupId is not null))
        {
            yield return new ValidationResult(
                "exportPath, nfsUserId, and nfsGroupId are only valid for NFS storage.",
                [nameof(ExportPath), nameof(NfsUserId), nameof(NfsGroupId), nameof(Protocol)]);
        }

        if (isNfs && (hasUsername || hasPassword || hasPrivateKey))
        {
            yield return new ValidationResult(
                "NFSv3 uses AUTH_UNIX user and group IDs instead of username/password authentication.",
                [nameof(Username), nameof(Password), nameof(PrivateKey), nameof(NfsUserId), nameof(NfsGroupId)]);
        }

        if (hasPrivateKey && Protocol != NetworkStorageProtocol.Sftp)
        {
            yield return new ValidationResult(
                "privateKey authentication is only valid for SFTP storage.",
                [nameof(PrivateKey), nameof(Protocol)]);
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum S3CompatibleObjectStorageProvider
{
    AwsS3,
    MinIo,
    DigitalOceanSpaces
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AzureBlobCredentialMode
{
    AccountKey,
    ConnectionString,
    SasUrl
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoogleCloudStorageCredentialMode
{
    CredentialsJson,
    CredentialsFilePath,
    WorkloadIdentity,
    DefaultCredentials
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(S3CompatibleObjectStorageParameters), typeDiscriminator: "s3Compatible")]
[JsonDerivedType(typeof(AzureBlobObjectStorageParameters), typeDiscriminator: "azureBlob")]
[JsonDerivedType(typeof(GoogleCloudStorageObjectStorageParameters), typeDiscriminator: "googleCloudStorage")]
public abstract class ObjectStorageParametersBase : StorageParametersBase
{
}

public sealed class S3CompatibleObjectStorageParameters : ObjectStorageParametersBase
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

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((Provider is S3CompatibleObjectStorageProvider.AwsS3 or S3CompatibleObjectStorageProvider.DigitalOceanSpaces) &&
            string.IsNullOrWhiteSpace(Region))
        {
            yield return new ValidationResult(
                "region is required for AwsS3 and DigitalOceanSpaces.",
                [nameof(Region)]);
        }

        if (Provider == S3CompatibleObjectStorageProvider.MinIo &&
            string.IsNullOrWhiteSpace(Endpoint))
        {
            yield return new ValidationResult(
                "endpoint is required for MinIo.",
                [nameof(Endpoint)]);
        }
    }
}

public sealed class AzureBlobObjectStorageParameters : ObjectStorageParametersBase
{
    [Required]
    public AzureBlobCredentialMode CredentialMode { get; init; }

    public string? ContainerName { get; init; }
    public string? AzureAccountName { get; init; }
    public string? AzureAccountKeySecretId { get; init; }
    public string? AzureConnectionStringSecretId { get; init; }
    public string? AzureSasUrlSecretId { get; init; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        switch (CredentialMode)
        {
            case AzureBlobCredentialMode.AccountKey:
                if (string.IsNullOrWhiteSpace(AzureAccountName))
                {
                    yield return new ValidationResult(
                        "azureAccountName is required when credentialMode is AccountKey.",
                        [nameof(AzureAccountName)]);
                }

                if (string.IsNullOrWhiteSpace(AzureAccountKeySecretId))
                {
                    yield return new ValidationResult(
                        "azureAccountKeySecretId is required when credentialMode is AccountKey.",
                        [nameof(AzureAccountKeySecretId)]);
                }
                break;

            case AzureBlobCredentialMode.ConnectionString:
                if (string.IsNullOrWhiteSpace(AzureConnectionStringSecretId))
                {
                    yield return new ValidationResult(
                        "azureConnectionStringSecretId is required when credentialMode is ConnectionString.",
                        [nameof(AzureConnectionStringSecretId)]);
                }
                break;

            case AzureBlobCredentialMode.SasUrl:
                if (string.IsNullOrWhiteSpace(AzureSasUrlSecretId))
                {
                    yield return new ValidationResult(
                        "azureSasUrlSecretId is required when credentialMode is SasUrl.",
                        [nameof(AzureSasUrlSecretId)]);
                }
                break;
        }
    }
}

public sealed class GoogleCloudStorageObjectStorageParameters : ObjectStorageParametersBase
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

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        switch (CredentialMode)
        {
            case GoogleCloudStorageCredentialMode.CredentialsJson:
                if (GcpCredentialsJson is null)
                {
                    yield return new ValidationResult(
                        "gcpCredentialsJson is required when credentialMode is CredentialsJson.",
                        [nameof(GcpCredentialsJson)]);
                }
                break;

            case GoogleCloudStorageCredentialMode.CredentialsFilePath:
                if (string.IsNullOrWhiteSpace(GcpCredentialsFilePath))
                {
                    yield return new ValidationResult(
                        "gcpCredentialsFilePath is required when credentialMode is CredentialsFilePath.",
                        [nameof(GcpCredentialsFilePath)]);
                }
                break;
        }
    }
}

public static class StorageParametersSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool TryDeserialize(
        StorageMethod method,
        string json,
        out StorageParametersBase? parameters,
        out string? error)
    {
        parameters = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Parameters JSON cannot be empty.";
            return false;
        }

        try
        {
            parameters = method switch
            {
                StorageMethod.Local => JsonSerializer.Deserialize<PosixLocalStorageParameters>(json, JsonOptions),
                StorageMethod.Network => JsonSerializer.Deserialize<StreamingNetworkStorageParameters>(json, JsonOptions),
                StorageMethod.ObjectStorage => DeserializeObjectStorageParameters(json),
                _ => null
            };

            if (parameters is null)
            {
                error = $"Unable to parse parameters for method '{method}'.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid parameters JSON: {ex.Message}";
            return false;
        }
    }

    public static IReadOnlyList<string> Validate(StorageMethod method, string json)
    {
        if (!TryDeserialize(method, json, out var parsed, out var parseError))
        {
            return [parseError ?? "Invalid parameters JSON."];
        }

        var results = new List<ValidationResult>();
        var validationContext = new ValidationContext(parsed!);
        Validator.TryValidateObject(parsed!, validationContext, results, validateAllProperties: true);

        return results.Select(x => x.ErrorMessage ?? "Invalid parameters value.").ToArray();
    }

    public static IReadOnlyList<string> Validate(StorageParametersBase parameters)
    {
        var results = new List<ValidationResult>();
        var validationContext = new ValidationContext(parameters);
        Validator.TryValidateObject(parameters, validationContext, results, validateAllProperties: true);
        return results.Select(x => x.ErrorMessage ?? "Invalid parameters value.").ToArray();
    }

    public static string Serialize(StorageMethod method, StorageParametersBase parameters)
    {
        var expectedType = method switch
        {
            StorageMethod.Local => typeof(PosixLocalStorageParameters),
            StorageMethod.Network => typeof(StreamingNetworkStorageParameters),
            StorageMethod.ObjectStorage => typeof(ObjectStorageParametersBase),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported storage method.")
        };

        if (!expectedType.IsInstanceOfType(parameters))
        {
            throw new ArgumentException(
                $"Parameters type '{parameters.GetType().Name}' does not match method '{method}'.",
                nameof(parameters));
        }

        return method == StorageMethod.ObjectStorage
            ? JsonSerializer.Serialize((ObjectStorageParametersBase)parameters, expectedType, JsonOptions)
            : JsonSerializer.Serialize(parameters, expectedType, JsonOptions);
    }

    internal static JsonSerializerOptions Options => JsonOptions;

    private static ObjectStorageParametersBase? DeserializeObjectStorageParameters(string json)
    {
        return JsonSerializer.Deserialize<ObjectStorageParametersBase>(json, JsonOptions);
    }
}
