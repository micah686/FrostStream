namespace YtDlpSharpLib.Progress;

/// <summary>
/// A single progress event parsed from yt-dlp output.
/// </summary>
public sealed record YtDlpProgress
{
    /// <summary>The lifecycle phase the event belongs to.</summary>
    public ProgressPhase Phase { get; init; }

    /// <summary>Percent complete (0-100), when reported.</summary>
    public double? Percent { get; init; }

    /// <summary>Estimated downloaded bytes, derived from <see cref="Percent"/> and <see cref="TotalBytes"/>.</summary>
    public long? DownloadedBytes { get; init; }

    /// <summary>Total bytes for the active stream, when reported.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Speed string, e.g., <c>"2.50MiB/s"</c>.</summary>
    public string? Speed { get; init; }

    /// <summary>ETA parsed into a <see cref="TimeSpan"/>, when reported in <c>HH:MM:SS</c> / <c>MM:SS</c> form.</summary>
    public TimeSpan? Eta { get; init; }

    /// <summary>Output filename or path emitted by yt-dlp post-processors, when present.</summary>
    public string? Destination { get; init; }

    /// <summary>Free-form post-processor message, e.g., <c>"Merging formats into ..."</c>.</summary>
    public string? Message { get; init; }

    /// <summary>The raw remainder of the line, for non-download phases.</summary>
    public string? AdditionalInfo { get; init; }

    /// <summary>The raw line as emitted by yt-dlp.</summary>
    public string? RawLine { get; init; }

    /// <summary>UTC timestamp of when the event was parsed.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
