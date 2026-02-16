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
