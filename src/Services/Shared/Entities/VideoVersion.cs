using System.ComponentModel.DataAnnotations;

namespace Shared.Entities;

/// <summary>
/// Represents the type of media content.
/// </summary>
public enum MediaType
{
    Unknown = 0,
    Video = 1,
    Audio = 2
}

/// <summary>
/// Represents the type of video variant.
/// </summary>
public enum VideoVariantType
{
    /// <summary>Original downloaded file from source platform.</summary>
    Original = 0,
    /// <summary>Transcoded variant (e.g., ffmpeg re-encode).</summary>
    Transcoded = 1
}

/// <summary>
/// Represents quality variants for both video (resolution) and audio (bitrate).
/// 
/// For Video content:
///   - Values represent vertical resolution in pixels (480, 720, 1080, 2160, 4320)
///   
/// For Audio content:
///   - Values represent bitrate in kbps (128, 192, 256, 320)
/// </summary>
public enum Quality
{
    Unknown = 0,
    
    // Video resolutions (height in pixels)
    P480 = 480,
    P720 = 720,
    P1080 = 1080,
    P1440 = 1440,
    P4K = 2160,
    P8K = 4320,
    
    // Audio bitrates (in kbps)
    K128 = 1128,  // Offset to avoid collision with video values
    K192 = 1192,
    K256 = 1256,
    K320 = 1320
}

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

    /// <summary>
    /// Type of media: Video or Audio.
    /// </summary>
    public MediaType MediaType { get; set; } = MediaType.Unknown;

    /// <summary>
    /// Type of variant: Original (downloaded) or Transcoded.
    /// </summary>
    public VideoVariantType VariantType { get; set; } = VideoVariantType.Original;

    /// <summary>
    /// Quality of the media:
    /// - For Video: resolution (P480=480p, P720=720p, P1080=1080p, P4K=2160p, etc.)
    /// - For Audio: bitrate (K128=128kbps, K192=192kbps, K256=256kbps, K320=320kbps)
    /// </summary>
    public Quality Quality { get; set; } = Quality.Unknown;

    /// <summary>
    /// For transcoded variants: the source video version ID that was used.
    /// Null for original downloads.
    /// </summary>
    public Guid? SourceVersionId { get; set; }

    /// <summary>
    /// Optional codec information (e.g., "h264", "h265", "av1").
    /// </summary>
    [MaxLength(20)]
    public string? Codec { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSize { get; set; }

    // Navigation properties
    public VideoInfo? VideoInfo { get; set; }
}
