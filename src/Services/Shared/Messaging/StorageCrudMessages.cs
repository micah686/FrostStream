using NodaTime;
using Shared.Storage;

namespace Shared.Messaging;

public sealed class StorageConfigDto
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public StorageMethod Method { get; init; }
    public string? Description { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
    public PosixLocalStorageParameters? Local { get; init; }
    public StreamingNetworkStorageParameters? Network { get; init; }
    public S3CompatibleObjectStorageParameters? ObjectS3Compatible { get; init; }
    public AzureBlobObjectStorageParameters? ObjectAzureBlob { get; init; }
    public GoogleCloudStorageObjectStorageParameters? ObjectGoogleCloudStorage { get; init; }
}

public sealed class StorageCreateLocalRequestMessage
{
    public required string Key { get; init; }
    public required PosixLocalStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageCreateStreamingRequestMessage
{
    public required string Key { get; init; }
    public required StreamingNetworkStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageCreateS3CompatibleObjectRequestMessage
{
    public required string Key { get; init; }
    public required S3CompatibleObjectStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageCreateAzureBlobObjectRequestMessage
{
    public required string Key { get; init; }
    public required AzureBlobObjectStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageCreateGoogleCloudStorageObjectRequestMessage
{
    public required string Key { get; init; }
    public required GoogleCloudStorageObjectStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageUpdateLocalRequestMessage
{
    public required string Key { get; init; }
    public required PosixLocalStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageUpdateStreamingRequestMessage
{
    public required string Key { get; init; }
    public required StreamingNetworkStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageUpdateS3CompatibleObjectRequestMessage
{
    public required string Key { get; init; }
    public required S3CompatibleObjectStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageUpdateAzureBlobObjectRequestMessage
{
    public required string Key { get; init; }
    public required AzureBlobObjectStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageUpdateGoogleCloudStorageObjectRequestMessage
{
    public required string Key { get; init; }
    public required GoogleCloudStorageObjectStorageParameters Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageGetRequestMessage
{
    public required string Key { get; init; }
}

public sealed class StorageListRequestMessage;

public sealed class StorageDeleteRequestMessage
{
    public required string Key { get; init; }
}

public sealed class StorageOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public StorageConfigDto? Entity { get; init; }
    public IReadOnlyList<StorageConfigDto>? Items { get; init; }
}
