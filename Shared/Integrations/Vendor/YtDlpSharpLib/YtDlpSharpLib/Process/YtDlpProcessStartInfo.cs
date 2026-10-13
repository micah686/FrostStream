namespace YtDlpSharpLib.Process;

/// <summary>
/// Describes everything needed to start a yt-dlp child process.
/// </summary>
public sealed record YtDlpProcessStartInfo
{
    /// <summary>Absolute or relative path to the yt-dlp executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The argv tokens to pass to the executable, excluding the executable itself.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory for the child process. <see langword="null"/> uses the parent's CWD.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to add or override.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Optional sink for stdout. Each line read from the child is forwarded here in addition
    /// to being placed on <see cref="IYtDlpProcess.StdoutLines"/>.
    /// </summary>
    public TextWriter? RawStdoutWriter { get; init; }

    /// <summary>
    /// Optional sink for stderr. Each line read from the child is forwarded here in addition
    /// to being placed on <see cref="IYtDlpProcess.StderrLines"/>.
    /// </summary>
    public TextWriter? RawStderrWriter { get; init; }
}
