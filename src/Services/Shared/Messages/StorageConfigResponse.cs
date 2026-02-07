namespace Shared.Messages;

/// <summary>
/// Response from DataBridge to Worker with storage configuration.
/// Carries a FluentStorage connection string and optional sub-path for the job.
/// </summary>
public record StorageConfigResponse
{
    /// <summary>
    /// The storage method to use for file transfer.
    /// </summary>
    public required StorageMethod Method { get; init; }

    /// <summary>
    /// FluentStorage connection string for the storage provider.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Sub-path within the storage for this job (e.g., remote directory, object prefix).
    /// </summary>
    public string? RemotePath { get; init; }
}
