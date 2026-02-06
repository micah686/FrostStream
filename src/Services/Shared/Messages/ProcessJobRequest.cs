namespace Shared.Messages;

/// <summary>
/// Request to process a media file.
/// Published by WebAPI, consumed by Worker.
/// </summary>
public record ProcessJobRequest
{
    /// <summary>
    /// Unique identifier for this job.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// The source file path or URL to process.
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// The desired final destination path.
    /// </summary>
    public required string DestinationPath { get; init; }

    /// <summary>
    /// Timestamp when the job was requested.
    /// </summary>
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
}
