namespace Shared.Messages;

/// <summary>
/// Event published by Worker when a file has been ingested and is ready for DataBridge to verify and commit.
/// </summary>
public record FileIngestedEvent
{
    /// <summary>
    /// The job ID this file belongs to.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// The staged file path (e.g., "{jobId}.part") relative to storage root.
    /// </summary>
    public required string StagedPath { get; init; }

    /// <summary>
    /// XxHash128 hex string computed during write.
    /// </summary>
    public required string XxHash { get; init; }

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>
    /// FluentStorage connection string so DataBridge can reach the file.
    /// </summary>
    public required string StorageConnectionString { get; init; }

    /// <summary>
    /// Worker that ingested this file.
    /// </summary>
    public required string WorkerId { get; init; }

    /// <summary>
    /// Movie title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Movie description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Movie release year.
    /// </summary>
    public required int ReleaseYear { get; init; }

    /// <summary>
    /// Movie duration in minutes.
    /// </summary>
    public required int DurationMinutes { get; init; }

    /// <summary>
    /// Timestamp when ingestion completed.
    /// </summary>
    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;
}
