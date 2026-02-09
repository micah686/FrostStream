namespace Shared.Messages;

/// <summary>
/// Request from a service to DataBridge asking for storage configuration.
/// </summary>
public record StorageConfigRequest
{
    /// <summary>
    /// The storage key to look up (e.g., "default", "premium-tier").
    /// </summary>
    public required string StorageKey { get; init; }
}
