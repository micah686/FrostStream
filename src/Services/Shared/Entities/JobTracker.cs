using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Shared.Entities;

public class JobTracker
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid JobId { get; set; }

    public Guid? VideoId { get; set; }

    [Required]
    [MaxLength(255)]
    public string IdempotencyKey { get; set; } = null!;

    [Required]
    [MaxLength(100)]
    public string StorageKey { get; set; } = null!;

    public string? StoragePath { get; set; }

    public ulong? FileHash { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public int RetryCount { get; set; }

    public string? ErrorDetails { get; set; }

    // Navigation properties
    public Job? Job { get; set; }
    public VideoInfo? VideoInfo { get; set; }
}
