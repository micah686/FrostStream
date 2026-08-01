namespace YtDlpSharpLib.Process;

/// <summary>
/// Factory responsible for creating a managed yt-dlp child process.
/// </summary>
public interface IYtDlpProcessFactory
{
    /// <summary>
    /// Creates a new <see cref="IYtDlpProcess"/> configured with the supplied start info.
    /// The process is not yet started; the caller must call <see cref="IYtDlpProcess.StartAsync"/>.
    /// </summary>
    IYtDlpProcess Create(YtDlpProcessStartInfo startInfo);
}
