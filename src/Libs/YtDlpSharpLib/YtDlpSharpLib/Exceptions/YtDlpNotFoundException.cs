namespace YtDlpSharpLib.Exceptions;

/// <summary>
/// Thrown when the yt-dlp executable cannot be located on the system.
/// </summary>
public sealed class YtDlpNotFoundException : YtDlpException
{
    /// <summary>The executable path that was attempted.</summary>
    public string AttemptedPath { get; }

    /// <inheritdoc />
    public YtDlpNotFoundException(string attemptedPath, Exception? inner = null)
        : base(
            $"Could not locate the yt-dlp executable at '{attemptedPath}'. " +
            "Ensure yt-dlp is installed and that its path is correctly configured.",
            inner: inner)
    {
        AttemptedPath = attemptedPath;
    }
}
