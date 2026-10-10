namespace YtDlpSharpLib.Exceptions;

/// <summary>
/// Thrown when yt-dlp output (JSON metadata, progress lines) cannot be parsed.
/// </summary>
public sealed class YtDlpParsingException : YtDlpException
{
    /// <inheritdoc />
    public YtDlpParsingException(string message, Exception? inner = null)
        : base(message, inner: inner)
    {
    }
}
