namespace YtDlpSharpLib.Downloads;

/// <summary>
/// Options for downloading every entry of a playlist.
/// </summary>
public record PlaylistDownloadOptions : DownloadOptions
{
    /// <summary>Optional playlist item selector, e.g., <c>"1-5,8,10-"</c>.</summary>
    public string? PlaylistItems { get; init; }
}
