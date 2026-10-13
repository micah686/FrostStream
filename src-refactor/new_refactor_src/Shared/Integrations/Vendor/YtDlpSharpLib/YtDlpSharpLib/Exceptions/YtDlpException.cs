namespace YtDlpSharpLib.Exceptions;

/// <summary>
/// Base exception for all YtDlpSharp errors.
/// </summary>
public abstract class YtDlpException : Exception
{
    /// <summary>The sanitized command line associated with the failure, if any.</summary>
    public string? YtDlpCommand { get; }

    /// <summary>The yt-dlp process exit code, if applicable.</summary>
    public int? ExitCode { get; }

    /// <inheritdoc />
    protected YtDlpException(
        string message,
        string? command = null,
        int? exitCode = null,
        Exception? inner = null)
        : base(message, inner)
    {
        YtDlpCommand = command;
        ExitCode = exitCode;
    }
}
