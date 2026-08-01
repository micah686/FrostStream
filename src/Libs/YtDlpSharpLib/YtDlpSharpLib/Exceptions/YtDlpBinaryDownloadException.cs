namespace YtDlpSharpLib.Exceptions;

/// <summary>
/// Thrown when one of the external binaries (yt-dlp, ffmpeg, ffprobe, Deno) cannot be downloaded.
/// </summary>
public sealed class YtDlpBinaryDownloadException : YtDlpException
{
    /// <summary>The URL that was being fetched when the failure occurred, when known.</summary>
    public string? Url { get; }

    /// <inheritdoc />
    public YtDlpBinaryDownloadException(string message, string? url = null, Exception? inner = null)
        : base(message, inner: inner)
    {
        Url = url;
    }
}
