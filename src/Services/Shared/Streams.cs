namespace Shared;

/// <summary>
/// JetStream stream name constants.
/// </summary>
public static class Streams
{
    /// <summary>
    /// Stream for all job-related subjects (froststream.job.>).
    /// </summary>
    public const string Jobs = "froststream-jobs";

    /// <summary>
    /// Stream for dead letter queue entries.
    /// </summary>
    public const string DeadLetter = "froststream-dlq";

    /// <summary>
    /// Stream for job progress updates (user-facing).
    /// </summary>
    public const string Progress = "froststream-progress";
}

/// <summary>
/// JetStream durable consumer name constants.
/// </summary>
public static class Consumers
{
    /// <summary>
    /// Durable consumer for file processing workers.
    /// </summary>
    public const string FileProcessors = "file-processors";
}
