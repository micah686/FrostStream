namespace Shared.Models;

/// <summary>
/// Result of a yt-dlp metadata fetch (--dump-json).
/// </summary>
public record YtDlpMetadata(
    string Id,
    string Platform,
    string Title,
    DateTime? SourceLastModified,
    string RawJson);

/// <summary>
/// Result of a full yt-dlp download.
/// </summary>
public record YtDlpDownloadResult(
    YtDlpMetadata Metadata,
    string LocalFilePath,
    ulong FileHash,
    long FileSize);
