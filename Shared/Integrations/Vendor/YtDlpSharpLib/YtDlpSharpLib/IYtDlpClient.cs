using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Progress;

namespace YtDlpSharpLib;

/// <summary>
/// Primary client for yt-dlp operations.
/// </summary>
public interface IYtDlpClient
{
    /// <summary>
    /// Default output folder for downloads. Per-call <see cref="Options.YtDlpFilesystemOptions.Paths"/> wins when set.
    /// </summary>
    string? OutputFolder { get; set; }

    /// <summary>
    /// Default output filename template. Per-call <see cref="Options.YtDlpFilesystemOptions.Output"/> wins when set.
    /// </summary>
    string? OutputFileTemplate { get; set; }

    /// <summary>
    /// Whether downloads should use restricted filenames by default. Per-call filename flags win when set.
    /// </summary>
    bool RestrictFilenames { get; set; }

    /// <summary>
    /// Whether downloads should overwrite existing files by default. Per-call overwrite flags win when set.
    /// </summary>
    bool OverwriteFiles { get; set; }

    /// <summary>
    /// Whether download errors should be ignored by default. Per-call error-handling flags win when set.
    /// </summary>
    bool IgnoreDownloadErrors { get; set; }

    /// <summary>Runs yt-dlp directly with the supplied <see cref="YtDlpOptions"/> and a single URL.</summary>
    Task<RunResult<YtDlpProcessResult>> RunWithOptionsAsync(
        string url,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Runs yt-dlp directly with the supplied <see cref="YtDlpOptions"/> and a batch of URLs.</summary>
    Task<RunResult<YtDlpProcessResult>> RunWithOptionsAsync(
        IEnumerable<string> urls,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Synchronously-named alias for <see cref="RunWithOptionsAsync(string, YtDlpOptions, string?, IProgress{YtDlpProgress}?, CancellationToken)"/>.</summary>
    Task<RunResult<YtDlpProcessResult>> RunWithOptions(
        string url,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Synchronously-named alias for <see cref="RunWithOptionsAsync(IEnumerable{string}, YtDlpOptions, string?, IProgress{YtDlpProgress}?, CancellationToken)"/>.</summary>
    Task<RunResult<YtDlpProcessResult>> RunWithOptions(
        IEnumerable<string> urls,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Retrieves structured video information without downloading any media.</summary>
    /// <param name="url">The video URL.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="flat">When <see langword="true"/>, request <c>--flat-playlist</c> so playlist URLs return shallow entries.</param>
    /// <param name="fetchComments">When <see langword="true"/>, instruct yt-dlp to populate the <c>comments</c> array.</param>
    /// <param name="overrideOptions">Additional <see cref="YtDlpOptions"/> merged on top of the metadata defaults.</param>
    Task<VideoInfo> GetVideoInfoAsync(
        string url,
        CancellationToken ct = default,
        bool flat = false,
        bool fetchComments = false,
        YtDlpOptions? overrideOptions = null);

    /// <summary>Retrieves structured video information without throwing for yt-dlp process failures.</summary>
    /// <inheritdoc cref="GetVideoInfoAsync(string, CancellationToken, bool, bool, YtDlpOptions?)"/>
    Task<RunResult<VideoInfo>> TryGetVideoInfoAsync(
        string url,
        CancellationToken ct = default,
        bool flat = false,
        bool fetchComments = false,
        YtDlpOptions? overrideOptions = null);

    /// <summary>Streams video metadata for each entry of a playlist.</summary>
    IAsyncEnumerable<VideoInfo> GetPlaylistInfoAsync(string url, CancellationToken ct = default, YtDlpOptions? overrideOptions = null);

    /// <summary>Downloads a video, optionally reporting progress through the supplied callback.</summary>
    Task DownloadAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Downloads a video without throwing for yt-dlp process failures.</summary>
    Task<RunResult> TryDownloadAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Downloads a video, exposing progress as an asynchronous stream.</summary>
    IAsyncEnumerable<YtDlpProgress> DownloadWithProgressAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Downloads audio only (maps to <c>-x --audio-format ...</c>).</summary>
    Task DownloadAudioAsync(
        string url,
        string outputDirectory,
        AudioDownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Downloads every entry of a video playlist.</summary>
    Task DownloadPlaylistAsync(
        string url,
        string outputDirectory,
        PlaylistDownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Downloads every entry of an audio-only playlist.</summary>
    Task DownloadAudioPlaylistAsync(
        string url,
        string outputDirectory,
        AudioPlaylistDownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Downloads metadata only (maps to <c>--write-info-json --skip-download</c>).</summary>
    Task DownloadMetadataAsync(
        string url,
        string outputDirectory,
        MetadataDownloadOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Downloads the live-chat replay (maps to <c>--write-subs --sub-langs live_chat --skip-download</c>).</summary>
    Task DownloadLiveChatAsync(
        string url,
        string outputDirectory,
        LiveChatDownloadOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Returns the yt-dlp version string.</summary>
    Task<string> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Runs yt-dlp's self-update passthrough (maps to <c>--update</c>).</summary>
    Task RunUpdateAsync(CancellationToken ct = default);
}
