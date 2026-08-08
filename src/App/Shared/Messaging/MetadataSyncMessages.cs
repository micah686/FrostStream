namespace Shared.Messaging;

public sealed record MetadataSyncUpsertMessage
{
    public required Guid MediaGuid { get; init; }
}
