namespace YtDlpSharpLib.Exceptions;

/// <summary>
/// Thrown when option values fail validation prior to launching yt-dlp.
/// </summary>
public sealed class YtDlpValidationException : YtDlpException
{
    /// <inheritdoc />
    public YtDlpValidationException(string message, Exception? inner = null)
        : base(message, inner: inner)
    {
    }
}
