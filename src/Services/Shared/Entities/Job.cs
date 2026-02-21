using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Shared.Entities;

public class Job
{
    [Key]
    public Guid JobId { get; set; }

    [Required]
    public string Url { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Pending";

    public string? ErrorMsg { get; set; }

    public int RetryCount { get; set; }

    [Required]
    [MaxLength(100)]
    public string StorageKey { get; set; } = null!;

    [Column(TypeName = "jsonb")]
    public string? Options { get; set; }

    // Navigation property
    public JobTracker? Tracker { get; set; }
}
