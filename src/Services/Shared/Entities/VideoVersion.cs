using System.ComponentModel.DataAnnotations;

namespace Shared.Entities;

public class VideoVersion
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid VideoId { get; set; }

    [Required]
    [MaxLength(255)]
    public string IdempotencyKey { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public string FileHash { get; set; } = null!;

    [Required]
    [MaxLength(100)]
    public string StorageKey { get; set; } = null!;

    [Required]
    public string StoragePath { get; set; } = null!;

    public int VersionNum { get; set; }

    // Navigation properties
    public VideoInfo? VideoInfo { get; set; }
}
