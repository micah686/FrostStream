namespace Shared.Messaging;

public sealed record MetadataSyncUpsertMessage
{
    public required Guid MediaGuid { get; init; }
}

public sealed record MetadataSyncRebuildRequestMessage;

public sealed record MetadataSyncRebuildResponseMessage
{
    public bool Accepted { get; init; }
    public string? ErrorMessage { get; init; }
}
