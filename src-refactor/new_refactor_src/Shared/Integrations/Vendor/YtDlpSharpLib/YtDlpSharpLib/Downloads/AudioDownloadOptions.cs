using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.Downloads;

/// <summary>
/// Options for an audio-only download (maps to <c>-x --audio-format ...</c>).
/// </summary>
public record AudioDownloadOptions : DownloadOptions
{
    /// <summary>The audio format to extract.</summary>
    public AudioConversionFormat AudioFormat { get; init; } = AudioConversionFormat.M4a;
}
