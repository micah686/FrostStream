using System.Threading.Channels;

namespace YtDlpSharpLib.Process;

/// <summary>
/// Represents a managed yt-dlp child process. Output is delivered through bounded
/// channels so reading and parsing run independently with natural back-pressure.
/// </summary>
public interface IYtDlpProcess : IAsyncDisposable
{
    /// <summary>Lines read from the child's stdout, in order.</summary>
    ChannelReader<string> StdoutLines { get; }

    /// <summary>Lines read from the child's stderr, in order.</summary>
    ChannelReader<string> StderrLines { get; }

    /// <summary>The child's exit code, once it has exited; otherwise <see langword="null"/>.</summary>
    int? ExitCode { get; }

    /// <summary><see langword="true"/> once the child has exited.</summary>
    bool HasExited { get; }

    /// <summary>The system process id of the child, once started.</summary>
    int? ProcessId { get; }

    /// <summary>Starts the child process and the background output readers.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Waits for the child to exit. Cancellation requests do not stop the child.</summary>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Kills the child immediately. When <paramref name="entireProcessTree"/> is
    /// <see langword="true"/>, descendants (e.g., spawned ffmpeg) are also terminated.</summary>
    void Kill(bool entireProcessTree);
}
