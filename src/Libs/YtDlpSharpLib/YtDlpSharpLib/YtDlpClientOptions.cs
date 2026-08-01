namespace YtDlpSharpLib;

/// <summary>
/// Configuration for <see cref="YtDlpClient"/>.
/// </summary>
public sealed record YtDlpClientOptions
{
    /// <summary>Path to the yt-dlp executable. Defaults to <c>"yt-dlp"</c> (resolved via PATH).</summary>
    public string YtDlpExecutablePath { get; init; } = "yt-dlp";

    /// <summary>Path to the ffmpeg executable used by yt-dlp for merging or conversion.</summary>
    public string FfmpegExecutablePath { get; init; } = "ffmpeg";

    /// <summary>
    /// Default output folder for downloads. Per-call <see cref="Options.YtDlpFilesystemOptions.Paths"/> wins when set.
    /// </summary>
    public string? OutputFolder { get; init; }

    /// <summary>
    /// Default output filename template. Per-call <see cref="Options.YtDlpFilesystemOptions.Output"/> wins when set.
    /// </summary>
    public string? OutputFileTemplate { get; init; }

    /// <summary>
    /// Whether downloads should use restricted filenames by default. Per-call filename flags win when set.
    /// </summary>
    public bool RestrictFilenames { get; init; }

    /// <summary>
    /// Whether downloads should overwrite existing files by default. Per-call overwrite flags win when set.
    /// </summary>
    public bool OverwriteFiles { get; init; }

    /// <summary>
    /// Whether download errors should be ignored by default. Per-call error-handling flags win when set.
    /// </summary>
    public bool IgnoreDownloadErrors { get; init; }

    /// <summary>Default maximum number of concurrent downloads for the execution scheduler.</summary>
    public int DownloadConcurrency { get; init; } = 2;

    /// <summary>Default maximum download rate passed to yt-dlp as <c>--limit-rate</c>, e.g. <c>500K</c> or <c>4.2M</c>.</summary>
    public string? DownloadLimitRate { get; init; }

    /// <summary>Default minimum rate passed to yt-dlp as <c>--throttled-rate</c>, e.g. <c>100K</c>.</summary>
    public string? DownloadThrottledRate { get; init; }

    /// <summary>Minimum delay between starting yt-dlp child processes. Does not limit already-running process concurrency.</summary>
    public TimeSpan? MinimumDelayBetweenProcessStarts { get; init; }

    /// <summary>
    /// Grace period given to yt-dlp to clean up after a graceful kill before the process tree is force-killed.
    /// </summary>
    public TimeSpan TerminationGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many lines of stderr to retain for error reporting on a non-zero exit.</summary>
    public int StderrTailLineCount { get; init; } = 100;

    /// <summary>Optional sink for raw yt-dlp stdout. Pass <see cref="Console.Out"/> for verbose console apps.</summary>
    public TextWriter? StdoutForwardingWriter { get; init; }

    /// <summary>Optional sink for raw yt-dlp stderr.</summary>
    public TextWriter? StderrForwardingWriter { get; init; }

    /// <summary>Environment variables to add or override for the yt-dlp child process.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}
