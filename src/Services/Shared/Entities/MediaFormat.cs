using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Shared.Entities;

/// <summary>
/// Represents technical media format details for a video version.
/// One-to-one relationship with VideoVersion.
/// </summary>
public class MediaFormat
{
    [Key]
    public long Id { get; set; }

    [Required]
    public Guid VideoVersionId { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Average overall bit rate in bits per second.
    /// </summary>
    public double? AverageBitRate { get; set; }

    /// <summary>
    /// Audio bit rate in bits per second.
    /// </summary>
    public double? AudioBitrate { get; set; }

    /// <summary>
    /// Audio sampling rate in Hz.
    /// </summary>
    public double? AudioSamplingRate { get; set; }

    /// <summary>
    /// Number of audio channels (e.g., 2 for stereo, 6 for 5.1).
    /// </summary>
    public short? AudioChannels { get; set; }

    /// <summary>
    /// Audio codec (e.g., "aac", "opus", "mp3").
    /// </summary>
    public string? AudioCodec { get; set; }

    /// <summary>
    /// Video width in pixels.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Video height in pixels.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Aspect ratio as string (e.g., "16:9", "4:3").
    /// </summary>
    public string? AspectRatio { get; set; }

    /// <summary>
    /// Video bit rate in bits per second.
    /// </summary>
    public double? VideoBitrate { get; set; }

    /// <summary>
    /// Frame rate in frames per second.
    /// </summary>
    public float? FrameRate { get; set; }

    /// <summary>
    /// Video codec (e.g., "h264", "h265", "av1", "vp9").
    /// </summary>
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Dynamic range (e.g., "SDR", "HDR10", "Dolby Vision").
    /// </summary>
    public string? DynamicRange { get; set; }

    /// <summary>
    /// Human-friendly resolution description (e.g., "1080p", "4K", "720p").
    /// </summary>
    public string? FriendlyVideoResolution { get; set; }

    // Navigation properties
    [ForeignKey("VideoVersionId")]
    public VideoVersion VideoVersion { get; set; } = null!;
}
