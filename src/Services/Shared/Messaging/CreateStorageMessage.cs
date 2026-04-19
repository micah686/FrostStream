namespace Shared.Messaging;

public sealed class CreateStorageMessage
{
    public required string Key { get; init; }
    public StorageMethod Method { get; init; }
    public required string Parameters { get; init; }
    public string? Description { get; init; }
    public DateTime RequestedAtUtc { get; init; }
}
