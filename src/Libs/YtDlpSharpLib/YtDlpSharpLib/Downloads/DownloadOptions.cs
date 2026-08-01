using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.Downloads;

/// <summary>
/// Options for a generic video download. Wraps a <see cref="YtDlpOptions"/> instance
/// so callers can compose any flag the library exposes, plus a small set of per-call
/// convenience flags that map onto the most common <see cref="YtDlpFilesystemOptions"/> /
/// <see cref="YtDlpGeneralOptions"/> overrides.
/// </summary>
public record DownloadOptions
{
    /// <summary>The underlying yt-dlp options used to build the command line.</summary>
    public YtDlpOptions YtDlp { get; init; } = new();

    /// <summary>
    /// When <see langword="true"/>, force <c>--abort-on-error</c> and disable any inherited
    /// <see cref="YtDlpClientOptions.IgnoreDownloadErrors"/> default for this call.
    /// </summary>
    public bool AbortOnError { get; init; }

    /// <summary>Per-call output filename template (maps to <c>--output</c>).</summary>
    public string? OutputTemplate { get; init; }

    /// <summary>Per-call override for filename restriction (<c>--restrict-filenames</c> / <c>--no-restrict-filenames</c>).</summary>
    public bool? RestrictFilenames { get; init; }

    /// <summary>Per-call override for overwrite behaviour (<c>--force-overwrites</c> / <c>--no-overwrites</c>).</summary>
    public bool? OverwriteFiles { get; init; }

    /// <summary>Per-call override for ignoring download errors (<c>--ignore-errors</c> / <c>--abort-on-error</c>).</summary>
    public bool? IgnoreDownloadErrors { get; init; }
}
