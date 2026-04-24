using NodaTime;

namespace Shared.Messaging;

public sealed class StorageConfigDto
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public StorageMethod Method { get; init; }
    public required string Parameters { get; init; }
    public string? Description { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}

public sealed class StorageCreateRequestMessage
{
    public required string Key { get; init; }
    public StorageMethod Method { get; init; }
    public required string Parameters { get; init; }
    public string? Description { get; init; }
}

public sealed class StorageUpdateRequestMessage
{
    public required string Key { get; init; }
    public StorageMethod Method { get; init; }
    public required string Parameters { get; init; }
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
