using System.ComponentModel.DataAnnotations;

namespace Shared.Entities;

/// <summary>
/// Represents the status of a DLQ (Dead Letter Queue) entry.
/// </summary>
public enum DlqEntryStatus
{
    /// <summary>Entry is pending review/processing.</summary>
    Pending = 0,
    /// <summary>Entry has been reviewed.</summary>
    Reviewed = 1,
    /// <summary>Entry has been retried successfully.</summary>
    Retried = 2,
    /// <summary>Entry has been discarded.</summary>
    Discarded = 3
}

/// <summary>
/// Database entity for storing Dead Letter Queue (DLQ) entries.
/// Tracks messages that failed processing after max retries.
/// </summary>
public class DlqEntry
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Unique identifier combining stream.consumer.sequence for idempotency.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string EntryKey { get; set; } = null!;

    /// <summary>
    /// The original NATS stream name.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string OriginalStream { get; set; } = null!;

    /// <summary>
    /// The original NATS consumer name.
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string OriginalConsumer { get; set; } = null!;

    /// <summary>
    /// The original NATS subject.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string OriginalSubject { get; set; } = null!;

    /// <summary>
    /// The sequence number in the original stream.
    /// </summary>
    public ulong OriginalSequence { get; set; }

    /// <summary>
    /// Number of delivery attempts before entering DLQ.
    /// </summary>
    public int DeliveryCount { get; set; }

    /// <summary>
    /// When the message entered the DLQ.
    /// </summary>
    public DateTimeOffset FailedAt { get; set; }

    /// <summary>
    /// When the entry was stored in the database.
    /// </summary>
    public DateTimeOffset StoredAt { get; set; }

    /// <summary>
    /// The error message/reason for failure.
    /// </summary>
    public string? ErrorReason { get; set; }

    /// <summary>
    /// The exception stack trace if available.
    /// </summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// The message payload (may be truncated for large payloads).
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// Content type of the payload.
    /// </summary>
    [MaxLength(100)]
    public string? PayloadContentType { get; set; }

    /// <summary>
    /// Size of the original payload in bytes.
    /// </summary>
    public long PayloadSize { get; set; }

    /// <summary>
    /// The message type (fully qualified name).
    /// </summary>
    [MaxLength(500)]
    public string? MessageType { get; set; }

    /// <summary>
    /// Correlation ID from the original message.
    /// </summary>
    [MaxLength(255)]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Job ID if this was a job-related message.
    /// </summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Current status of the DLQ entry.
    /// </summary>
    public DlqEntryStatus Status { get; set; } = DlqEntryStatus.Pending;

    /// <summary>
    /// When the status was last updated.
    /// </summary>
    public DateTimeOffset? StatusUpdatedAt { get; set; }

    /// <summary>
    /// Notes from manual review.
    /// </summary>
    public string? ReviewNotes { get; set; }
}
