namespace YtDlpSharpLib.Exceptions;

/// <summary>
/// Thrown when the yt-dlp process exits with a non-zero exit code.
/// </summary>
public sealed class YtDlpProcessException : YtDlpException
{
    /// <summary>The last lines captured from stderr at the time of failure.</summary>
    public string LastStderrLines { get; }

    /// <inheritdoc />
    public YtDlpProcessException(
        string message,
        string? command,
        int? exitCode,
        string lastStderrLines,
        Exception? inner = null)
        : base(message, command, exitCode, inner)
    {
        LastStderrLines = lastStderrLines;
    }
}
