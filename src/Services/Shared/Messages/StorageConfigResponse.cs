namespace Shared.Messages;

/// <summary>
/// Response from DataBridge to Worker with storage configuration.
/// </summary>
public record StorageConfigResponse
{
    /// <summary>
    /// The storage method to use for file transfer.
    /// </summary>
    public required StorageMethod Method { get; init; }

    /// <summary>
    /// For LocalStaging: the shared staging directory path.
    /// </summary>
    public string? StagingPath { get; init; }

    /// <summary>
    /// For ObjectStore: the bucket name to use.
    /// </summary>
    public string? ObjectStoreBucket { get; init; }

    /// <summary>
    /// For DirectExternal: the presigned URL or endpoint.
    /// </summary>
    public string? ExternalEndpoint { get; init; }
}
