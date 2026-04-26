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
    public StreamingNetworkStorageParameters? Streaming { get; init; }
    public ObjectStorageParameters? Object { get; init; }
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

public sealed class StorageCreateObjectRequestMessage
{
    public required string Key { get; init; }
    public required ObjectStorageParameters Parameters { get; init; }
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

public sealed class StorageUpdateObjectRequestMessage
{
    public required string Key { get; init; }
    public required ObjectStorageParameters Parameters { get; init; }
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
