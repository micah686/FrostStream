using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Progress;

namespace YtDlpSharpLib.Scheduling;

/// <summary>
/// A single download job submitted to the execution scheduler. The <see cref="Kind"/>
/// selects which client method is invoked and which of the typed option records is honoured.
/// </summary>
public sealed record DownloadRequest
{
    /// <summary>The URL to download.</summary>
    public required string Url { get; init; }

    /// <summary>The output directory the downloaded media should be written to.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>The kind of download to perform. Defaults to <see cref="DownloadRequestKind.Video"/>.</summary>
    public DownloadRequestKind Kind { get; init; } = DownloadRequestKind.Video;

    /// <summary>Per-request progress reporter, when set.</summary>
    public IProgress<YtDlpProgress>? Progress { get; init; }

    /// <summary>
    /// Options for <see cref="DownloadRequestKind.Video"/>. Also used as a fallback for legacy
    /// callers that populated <see cref="Options"/> without specifying a <see cref="Kind"/>.
    /// </summary>
    public DownloadOptions? DownloadOptions { get; init; }

    /// <summary>Options for <see cref="DownloadRequestKind.Audio"/>.</summary>
    public AudioDownloadOptions? AudioOptions { get; init; }

    /// <summary>Options for <see cref="DownloadRequestKind.Playlist"/>.</summary>
    public PlaylistDownloadOptions? PlaylistOptions { get; init; }

    /// <summary>Options for <see cref="DownloadRequestKind.AudioPlaylist"/>.</summary>
    public AudioPlaylistDownloadOptions? AudioPlaylistOptions { get; init; }

    /// <summary>Options for <see cref="DownloadRequestKind.Metadata"/>.</summary>
    public MetadataDownloadOptions? MetadataOptions { get; init; }

    /// <summary>Options for <see cref="DownloadRequestKind.LiveChat"/>.</summary>
    public LiveChatDownloadOptions? LiveChatOptions { get; init; }

    /// <summary>Backwards-compatible alias for <see cref="DownloadOptions"/>.</summary>
    public DownloadOptions? Options
    {
        get => DownloadOptions;
        init => DownloadOptions = value;
    }
}
