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

    public const string JobStart = "databridge.job.start";
    public const string JobProgress = "databridge.job.progress";
    public const string VideoCommit = "databridge.video.commit";
    public const string JobFail = "databridge.job.fail";
    public const string GetNextVersion = "databridge.version.next";
    public const string JobStatus = "databridge.job.status";
    public const string JobLinkComplete = "databridge.job.link_complete";

    /// <summary>
    /// Subject for dead letter queue messages.
    /// </summary>
    public const string DeadLetter = "froststream.dlq.entry";

    /// <summary>
    /// Subject for job progress updates (user-facing progress stream).
    /// </summary>
    public const string JobProgressStream = "froststream.job.progress.stream";
}
