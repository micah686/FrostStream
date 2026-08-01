namespace YtDlpSharpLib.Provisioning;

/// <summary>
/// Result of <see cref="IYtDlpBinaryDownloader.DownloadAllAsync"/>. Each property is the absolute
/// path to the downloaded (or pre-existing) binary, or <see langword="null"/> if it was not requested.
/// </summary>
public sealed record BinaryDownloadResult
{
    /// <summary>Path to the yt-dlp executable, when requested.</summary>
    public string? YtDlpPath { get; init; }

    /// <summary>Path to the ffmpeg executable, when requested.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Path to the ffprobe executable, when requested.</summary>
    public string? FfprobePath { get; init; }

    /// <summary>Path to the Deno executable, when requested.</summary>
    public string? DenoPath { get; init; }

    /// <summary>
    /// Path to the directory that should be passed to yt-dlp's <c>--plugin-dirs</c> (the parent of the
    /// extracted <c>yt_dlp_plugins</c> tree), when the bgutil plugin was requested; otherwise <see langword="null"/>.
    /// </summary>
    public string? BgUtilPluginDir { get; init; }
}
