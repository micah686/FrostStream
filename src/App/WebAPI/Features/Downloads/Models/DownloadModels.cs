using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using WebAPI.Features.Downloads.Controllers;
using WebAPI.Features.OptionPresets.Controllers;

namespace WebAPI.Features.Downloads.Models;

/// <summary>Body for <see cref="DownloadsController.Download"/> - simple video download.</summary>
public sealed class DownloadRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public required string StorageKey { get; init; }

    [DefaultValue(false)]
    public bool ForceDownload { get; init; } = false;

    public string? RequestedBy { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

/// <summary>Body for <see cref="DownloadsController.DownloadAudio"/> - simple audio download (always MP3).</summary>
public sealed class DownloadAudioRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public required string StorageKey { get; init; }

    [DefaultValue(false)]
    public bool ForceDownload { get; init; } = false;

    public string? RequestedBy { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

/// <summary>Body for <see cref="DownloadsController.DownloadWithPreset"/> - download driven by a stored option preset.</summary>
public sealed class DownloadPresetRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public required string StorageKey { get; init; }

    [DefaultValue(false)]
    public bool ForceDownload { get; init; } = false;

    public string? RequestedBy { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Stored option-preset key (see <see cref="OptionPresetsController"/>).</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string PresetKey { get; init; }

    /// <summary>Reference to a Netscape cookie file stored at OpenBAO <c>cookies/{key}</c>.</summary>
    public string? CookieKey { get; init; }
}

public sealed record DownloadRequestResponse(Guid JobId, Guid CorrelationId);
