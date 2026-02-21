using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Shared.Entities;

public class VideoInfo
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string VideoUrl { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Platform { get; set; } = null!;

    public DateTime? SourceLastModified { get; set; }

    public DateTime CreatedAt { get; set; }

    [Column(TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    [MaxLength(255)]
    public string? IdempotencyKey { get; set; }

    public bool IsDirty { get; set; } = true;

    // Navigation properties
    public ICollection<VideoVersion> Versions { get; set; } = new List<VideoVersion>();
    public ICollection<JobTracker> JobTrackers { get; set; } = new List<JobTracker>();
}
