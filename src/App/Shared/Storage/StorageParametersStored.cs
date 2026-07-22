using System.Text.Json.Serialization;

namespace Shared.Storage;

/// <summary>
/// Variants of storage parameters that omit sensitive credential fields. These
/// are what gets persisted to the application database, returned in API/list
/// responses, and carried over messaging buses. Plaintext secrets live only in
/// the secret store and are merged back in at use time when constructing an
/// IStore.
/// </summary>
public abstract class StorageParametersStoredBase
{
}

public sealed class PosixLocalStorageStored : StorageParametersStoredBase
{
    public LocalStorageProtocol Protocol { get; init; }

    public required string Path { get; init; }
}

public sealed class StreamingNetworkStorageStored : StorageParametersStoredBase
{
    public NetworkStorageProtocol Protocol { get; init; }

    public required string Host { get; init; }

    public int? Port { get; init; }

    public string? Username { get; init; }

    public string? BasePath { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(S3CompatibleObjectStorageStored), typeDiscriminator: "s3Compatible")]
[JsonDerivedType(typeof(AzureBlobObjectStorageStored), typeDiscriminator: "azureBlob")]
[JsonDerivedType(typeof(GoogleCloudStorageObjectStorageStored), typeDiscriminator: "googleCloudStorage")]
public abstract class ObjectStorageParametersStoredBase : StorageParametersStoredBase
{
}

public sealed class S3CompatibleObjectStorageStored : ObjectStorageParametersStoredBase
{
    public S3CompatibleObjectStorageProvider Provider { get; init; }

    public required string BucketName { get; init; }

    public string? Region { get; init; }

    public string? Endpoint { get; init; }

    public bool HasSessionToken { get; init; }

    public bool ForcePathStyle { get; init; }

    public bool? UseSsl { get; init; }
}

public sealed class AzureBlobObjectStorageStored : ObjectStorageParametersStoredBase
{
    public AzureBlobCredentialMode CredentialMode { get; init; }

    public string? ContainerName { get; init; }

    public string? AzureAccountName { get; init; }
}

public sealed class GoogleCloudStorageObjectStorageStored : ObjectStorageParametersStoredBase
{
    public required string BucketName { get; init; }

    public GoogleCloudStorageCredentialMode CredentialMode { get; init; }

    public string? GcpCredentialsFilePath { get; init; }

    public string? GcpProjectId { get; init; }
}
