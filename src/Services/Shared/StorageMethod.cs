namespace Shared;

/// <summary>
/// Defines the available storage methods for file transfer.
/// </summary>
public enum StorageMethod
{
    /// <summary>
    /// Local staging via shared filesystem (e.g., Docker volume, K8s emptyDir).
    /// Worker stages file locally, DataBridge picks it up from the shared path.
    /// </summary>
    LocalStaging,

    /// <summary>
    /// Direct streaming via NATS Object Store or chunked messages.
    /// </summary>
    DirectStreaming,

    /// <summary>
    /// NATS Object Store for larger files with built-in chunking.
    /// </summary>
    ObjectStore,

    /// <summary>
    /// Direct upload to external storage (S3, Azure Blob, etc.) with URL signaling.
    /// </summary>
    DirectExternal
}
