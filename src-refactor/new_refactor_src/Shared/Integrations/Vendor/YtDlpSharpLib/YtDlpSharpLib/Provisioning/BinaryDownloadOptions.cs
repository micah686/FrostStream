namespace YtDlpSharpLib.Provisioning;

/// <summary>
/// Per-call options for <see cref="IYtDlpBinaryDownloader.DownloadAllAsync"/>.
/// </summary>
public sealed record BinaryDownloadOptions
{
    /// <summary>
    /// Directory the binaries should be written to. <see langword="null"/> falls back to
    /// <see cref="YtDlpBinaryDownloaderOptions.DefaultDirectory"/>, then to the current working directory.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// When <see langword="true"/>, binaries already present at the target path are not redownloaded.
    /// </summary>
    public bool SkipExisting { get; init; } = true;

    /// <summary>Whether to download the yt-dlp binary.</summary>
    public bool DownloadYtDlp { get; init; } = true;

    /// <summary>Whether to download ffmpeg.</summary>
    public bool DownloadFfmpeg { get; init; } = true;

    /// <summary>Whether to download ffprobe.</summary>
    public bool DownloadFfprobe { get; init; } = true;

    /// <summary>Whether to download the Deno JavaScript runtime.</summary>
    public bool DownloadDeno { get; init; } = true;

    /// <summary>
    /// Whether to download and extract the bgutil-ytdlp-pot-provider yt-dlp plugin into a
    /// <c>yt-dlp-plugins</c> directory under the target directory. Off by default; the Worker opts in.
    /// </summary>
    public bool DownloadBgUtilPlugin { get; init; }
}
