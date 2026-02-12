namespace Shared;

/// <summary>
/// NATS subject constants for inter-service messaging.
/// </summary>
public static class Subjects
{
    /// <summary>
    /// Subject for file processing requests, consumed by workers via queue group.
    /// </summary>
    public const string DownloadFile = "froststream.job.download.file";

    /// <summary>
    /// Subject for storage config request/reply, handled by DataBridge.
    /// </summary>
    public const string StorageConfig = "froststream.config.storage";
}
