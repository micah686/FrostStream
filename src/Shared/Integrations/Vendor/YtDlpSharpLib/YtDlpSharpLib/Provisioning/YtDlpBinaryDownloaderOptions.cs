namespace YtDlpSharpLib.Provisioning;

/// <summary>
/// Configuration for <see cref="YtDlpBinaryDownloader"/>.
/// </summary>
public sealed record YtDlpBinaryDownloaderOptions
{
    /// <summary>
    /// Default directory used when a per-call directory is not supplied.
    /// <see langword="null"/> falls back to <see cref="Directory.GetCurrentDirectory"/> at the moment of download.
    /// </summary>
    public string? DefaultDirectory { get; init; }

    /// <summary>
    /// Timeout applied to internally-owned <see cref="HttpClient"/> instances. Ignored when an
    /// externally-supplied <see cref="HttpClient"/> is injected.
    /// </summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// User-Agent header set on requests when the downloader owns the <see cref="HttpClient"/>.
    /// </summary>
    public string UserAgent { get; init; } = "YtDlpSharpLib";

    /// <summary>
    /// Override URL for the yt-dlp <c>releases/latest/download/</c> base. Use the trailing slash form, e.g.
    /// <c>"https://github.com/yt-dlp/yt-dlp/releases/latest/download/"</c>.
    /// </summary>
    public string YtDlpReleaseBaseUrl { get; init; } = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    /// <summary>Override URL for the ffbinaries.com latest-version JSON endpoint.</summary>
    public string FfBinariesApiUrl { get; init; } = "https://ffbinaries.com/api/v1/version/latest";

    /// <summary>Override URL for the Deno latest-release version text file.</summary>
    public string DenoVersionUrl { get; init; } = "https://dl.deno.land/release-latest.txt";

    /// <summary>
    /// Format string for the Deno download URL. Token <c>{0}</c> is the version string, token <c>{1}</c> is the platform target.
    /// </summary>
    public string DenoDownloadUrlTemplate { get; init; } = "https://dl.deno.land/release/{0}/deno-{1}.zip";

    /// <summary>
    /// Release tag of the bgutil-ytdlp-pot-provider plugin to download. Must match the version of the
    /// provider server the tokens are minted by (the project requires plugin/server versions to match).
    /// </summary>
    public string BgUtilPluginVersion { get; init; } = "1.3.1";

    /// <summary>
    /// Format string for the bgutil plugin release zip URL. Token <c>{0}</c> is <see cref="BgUtilPluginVersion"/>.
    /// The archive contains a <c>yt_dlp_plugins</c> package tree that is extracted into a
    /// <c>yt-dlp-plugins</c> directory passed to yt-dlp's <c>--plugin-dirs</c>.
    /// </summary>
    public string BgUtilPluginUrlTemplate { get; init; } =
        "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/{0}/bgutil-ytdlp-pot-provider.zip";

    /// <summary>Name of the directory (under the target directory) the plugin is extracted into.</summary>
    public string BgUtilPluginDirectoryName { get; init; } = "yt-dlp-plugins";
}
