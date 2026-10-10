namespace YtDlpSharpLib.Process;

/// <summary>
/// Captured output of a yt-dlp child process invocation.
/// </summary>
public sealed record YtDlpProcessResult
{
    /// <summary>The yt-dlp exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>The full standard output as a single string.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>The full standard error as a single string.</summary>
    public required string StandardError { get; init; }

    /// <summary>Standard output split into lines, in order of emission.</summary>
    public required IReadOnlyList<string> StandardOutputLines { get; init; }

    /// <summary>Standard error split into lines, in order of emission.</summary>
    public required IReadOnlyList<string> StandardErrorLines { get; init; }
}
