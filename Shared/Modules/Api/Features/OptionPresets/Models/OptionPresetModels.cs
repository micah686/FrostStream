using System.ComponentModel.DataAnnotations;
using NodaTime;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.OptionPresets.Models;

public sealed class OptionPresetCreateRequest
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(2000)]
    public string? Description { get; init; }

    [Required]
    public required YtDlpOptions YtDlpOptions { get; init; }
}

public sealed class OptionPresetUpdateRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(2000)]
    public string? Description { get; init; }

    [Required]
    public required YtDlpOptions YtDlpOptions { get; init; }
}

public sealed class OptionPresetResponse
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required YtDlpOptions YtDlpOptions { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
