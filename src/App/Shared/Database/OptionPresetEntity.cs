using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NodaTime;

namespace Shared.Database;

/// <summary>
/// A reusable, named yt-dlp option set referenced by <c>PresetKey</c> on a download
/// request. The serialized YtDlpOptions JSON lives in <see cref="YtDlpOptionsJson"/>;
/// DataBridge deserializes it on flow entry and forwards it to the Worker.
/// </summary>
public class OptionPresetEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; set; }

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>JSON-serialized <c>YtDlpSharpLib.Options.YtDlpOptions</c>. Stored as <c>jsonb</c>.</summary>
    [Required]
    public required string YtDlpOptionsJson { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }
}
