namespace YtDlpSharpLib.Scheduling;

/// <summary>
/// The outcome of a single scheduled download.
/// </summary>
public sealed record DownloadResult
{
    /// <summary>The originating request.</summary>
    public required DownloadRequest Request { get; init; }

    /// <summary>Whether the download completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Captured error output, when <see cref="Success"/> is <see langword="false"/>.</summary>
    public string ErrorOutput { get; init; } = string.Empty;

    /// <summary>The yt-dlp exit code, when known.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The exception that ended the job, when not successful.</summary>
    public Exception? Error { get; init; }

    /// <summary>The URL processed (alias for <see cref="DownloadRequest.Url"/>).</summary>
    public string Url => Request.Url;

    /// <summary>Creates a successful result for the supplied request.</summary>
    public static DownloadResult Succeeded(DownloadRequest request) => new()
    {
        Request = request,
        Success = true,
        ExitCode = 0
    };

    /// <summary>Creates a failed result for the supplied request.</summary>
    public static DownloadResult Failed(DownloadRequest request, string errorOutput, Exception? error = null, int? exitCode = null) => new()
    {
        Request = request,
        Success = false,
        ErrorOutput = errorOutput,
        Error = error,
        ExitCode = exitCode
    };
}
