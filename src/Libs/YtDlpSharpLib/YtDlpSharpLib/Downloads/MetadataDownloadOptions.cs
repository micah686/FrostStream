namespace YtDlpSharpLib.Downloads;

/// <summary>
/// Options for retrieving metadata only (maps to <c>--write-info-json --skip-download</c>).
/// </summary>
public record MetadataDownloadOptions : DownloadOptions
{
    /// <summary>Whether to also write the thumbnail next to the info-json.</summary>
    public bool WriteThumbnail { get; init; }

    /// <summary>Whether to also write subtitle sidecar files.</summary>
    public bool WriteSubtitles { get; init; }

    /// <summary>Optional comma-separated subtitle languages (e.g., <c>"en,ja"</c>).</summary>
    public string? SubtitleLanguages { get; init; }
}
