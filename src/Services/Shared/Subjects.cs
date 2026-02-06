namespace Shared;

/// <summary>
/// NATS subject names used across services.
/// </summary>
public static class Subjects
{
    /// <summary>
    /// Subject for job processing requests.
    /// Workers subscribe with a queue group for load balancing.
    /// </summary>
    public const string JobProcess = "jobs.process";

    /// <summary>
    /// Subject for requesting storage configuration from DataBridge.
    /// Uses request/reply pattern.
    /// </summary>
    public const string StorageConfig = "databridge.storage.config";

    /// <summary>
    /// Subject for signaling that a file has been staged and is ready.
    /// </summary>
    public const string FileStaged = "databridge.file.staged";
}
