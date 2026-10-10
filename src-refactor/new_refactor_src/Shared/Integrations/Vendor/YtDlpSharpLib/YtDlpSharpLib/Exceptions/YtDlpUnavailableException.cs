namespace YtDlpSharpLib.Exceptions;

/// <summary>
/// Thrown when yt-dlp reports a known domain condition such as a removed,
/// geo-blocked, or otherwise unavailable video.
/// </summary>
public class YtDlpUnavailableException : YtDlpException
{
    /// <inheritdoc />
    public YtDlpUnavailableException(
        string message,
        string? command = null,
        int? exitCode = null,
        Exception? inner = null)
        : base(message, command, exitCode, inner)
    {
    }
}
