using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shared.Messaging;
using WebAPI.Features.Downloads.Controllers;
using WebAPI.Features.OptionPresets.Controllers;
using YtDlpSharpLib.Options;

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

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Key of one of the authenticated user's cookie profiles. Resolved server-side to the
    /// user-scoped secret path; the worker only ever receives the resolved opaque path.
    /// </summary>
    public string? CookieProfileKey { get; init; }

    /// <summary>Scheduling priority 0–100 (default 0). Higher values run before lower ones when multiple
    /// jobs are waiting for a download slot.</summary>
    [Range(0, 100)]
    public int Priority { get; init; } = 0;

    public SponsorBlockRequest? SponsorBlock { get; init; }

    /// <summary>
    /// Optional yt-dlp options passed through to the worker's yt-dlp invocation (e.g. subtitle,
    /// thumbnail, or info-json capture). The <see cref="SponsorBlock"/> section, when supplied,
    /// replaces the SponsorBlock group of these options; worker operational defaults still win.
    /// </summary>
    public YtDlpOptions? YtDlpOptions { get; init; }

    /// <summary>When true, yt-dlp fetches comments and they are persisted to the database and indexed in Typesense.</summary>
    [DefaultValue(false)]
    public bool FetchComments { get; init; } = false;
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

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Key of one of the authenticated user's cookie profiles. Resolved server-side to the
    /// user-scoped secret path; the worker only ever receives the resolved opaque path.
    /// </summary>
    public string? CookieProfileKey { get; init; }

    /// <summary>Scheduling priority 0–100 (default 0). Higher values run before lower ones when multiple
    /// jobs are waiting for a download slot.</summary>
    [Range(0, 100)]
    public int Priority { get; init; } = 0;

    public SponsorBlockRequest? SponsorBlock { get; init; }

    /// <summary>When true, yt-dlp fetches comments and they are persisted to the database and indexed in Typesense.</summary>
    [DefaultValue(false)]
    public bool FetchComments { get; init; } = false;
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

    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Stored option-preset key (see <see cref="OptionPresetsController"/>).</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string PresetKey { get; init; }

    /// <summary>
    /// Key of one of the authenticated user's cookie profiles. Resolved server-side to the
    /// user-scoped secret path; the worker only ever receives the resolved opaque path.
    /// </summary>
    public string? CookieProfileKey { get; init; }

    /// <summary>Scheduling priority 0–100 (default 0). Higher values run before lower ones when multiple
    /// jobs are waiting for a download slot.</summary>
    [Range(0, 100)]
    public int Priority { get; init; } = 0;

    /// <summary>When true, yt-dlp fetches comments and they are persisted to the database and indexed in Typesense.</summary>
    [DefaultValue(false)]
    public bool FetchComments { get; init; } = false;
}

/// <summary>Body for <c>PATCH /api/downloads/{jobId}/priority</c>.</summary>
public sealed class UpdatePriorityRequest
{
    [Range(0, 100)]
    public required int Priority { get; init; }
}

/// <summary>Body for <c>POST /api/downloads/{jobId}/cancel</c>.</summary>
public sealed class CancelDownloadApiRequest
{
    [StringLength(512)]
    public string? Reason { get; init; }
}

public sealed record CancelDownloadApiResponse(DownloadJobState State);

/// <summary>Body for <c>GET /api/downloads/queue/{jobId}/media</c>.</summary>
public sealed record DownloadQueueMediaDto(Guid MediaGuid);

public sealed class SponsorBlockRequest
{
    /// <summary>SponsorBlock categories to mark as chapters, e.g. <c>all,-preview</c>.</summary>
    [StringLength(255)]
    [RegularExpression("^[a-z0-9_,-]+$")]
    public string? MarkCategories { get; init; }

    /// <summary>SponsorBlock categories to remove from the media file, e.g. <c>sponsor,selfpromo</c>.</summary>
    [StringLength(255)]
    [RegularExpression("^[a-z0-9_,-]+$")]
    public string? RemoveCategories { get; init; }

    [StringLength(512)]
    public string? ChapterTitleTemplate { get; init; }

    [Url]
    [StringLength(2048)]
    public string? ApiUrl { get; init; }

    public bool Disable { get; init; }
}

public sealed record DownloadRequestResponse(Guid JobId, Guid CorrelationId);

/// <summary>
/// Returned by <c>POST /api/downloads/video</c> when the URL was an unambiguous playlist
/// container and the request was auto-routed into the playlist pipeline. Carries a playlist
/// identifier instead of a job identifier; <see cref="Kind"/> lets callers discriminate.
/// </summary>
public sealed record DownloadRoutedToPlaylistResponse(Guid PlaylistId, Guid CorrelationId, string Kind = "playlist");
