namespace Shared.Messages;

/// <summary>
/// Event published by Worker when a file has been staged and is ready for DataBridge to process.
/// </summary>
public record FileStagedEvent
{
    /// <summary>
    /// The job ID this file belongs to.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// The local path where the file is staged (in shared volume).
    /// </summary>
    public required string LocalPath { get; init; }

    /// <summary>
    /// The final destination path where the file should be moved/uploaded.
    /// </summary>
    public required string FinalDestination { get; init; }

    /// <summary>
    /// SHA256 checksum of the staged file for integrity verification.
    /// </summary>
    public required string Checksum { get; init; }

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>
    /// Worker that staged this file.
    /// </summary>
    public required string WorkerId { get; init; }

    /// <summary>
    /// Timestamp when staging completed.
    /// </summary>
    public DateTimeOffset StagedAt { get; init; } = DateTimeOffset.UtcNow;
}
