namespace Shared.Messages;

/// <summary>
/// Response containing a storage configuration looked up by key.
/// </summary>
/// <param name="Found">Whether a config was found for the requested key.</param>
/// <param name="Key">The storage config key.</param>
/// <param name="Method">The storage method (PosixLocal, StreamingNetwork, ObjectStorage).</param>
/// <param name="Parameters">JSON blob with provider-specific connection parameters.</param>
/// <param name="Description">Human-readable description of the storage config.</param>
public record StorageConfigResponse(
    bool Found,
    string? Key,
    StorageMethod? Method,
    string? Parameters,
    string? Description);
