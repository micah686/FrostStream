using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.Downloads;

/// <summary>
/// Options for downloading every entry of a playlist as audio-only files.
/// </summary>
public record AudioPlaylistDownloadOptions : PlaylistDownloadOptions
{
    /// <summary>The audio format to extract.</summary>
    public AudioConversionFormat AudioFormat { get; init; } = AudioConversionFormat.M4a;
}
