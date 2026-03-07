using System.ComponentModel.DataAnnotations;

namespace Shared.Entities;

public class PendingJobLink
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid PendingJobId { get; set; }

    [Required]
    public Guid SourceJobId { get; set; }

    [Required]
    [MaxLength(255)]
    public string IdempotencyKey { get; set; } = null!;

    public Guid? VideoId { get; set; }

    public Guid? ExistingVersionId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Job? PendingJob { get; set; }
    public Job? SourceJob { get; set; }
    public VideoInfo? VideoInfo { get; set; }
    public VideoVersion? VideoVersion { get; set; }
}
