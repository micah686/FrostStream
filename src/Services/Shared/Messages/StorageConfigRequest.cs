namespace Shared.Messages;

/// <summary>
/// Request to retrieve a storage configuration by key.
/// </summary>
/// <param name="StorageKey">The unique key identifying the storage configuration.</param>
public record StorageConfigRequest(string StorageKey);
