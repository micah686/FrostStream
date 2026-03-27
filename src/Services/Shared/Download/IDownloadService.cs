namespace Shared.Download;

/// <summary>
/// Represents the type of media content that can be downloaded.
/// </summary>
public enum MediaContentType
{
    /// <summary>Video content with both audio and video streams.</summary>
    Video,
    /// <summary>Audio-only content.</summary>
    Audio,
    /// <summary>Best available quality (platform-dependent).</summary>
    Best,
    /// <summary>Worst available quality (platform-dependent).</summary>
    Worst
}

/// <summary>
/// Options for downloading media content.
/// </summary>
public record DownloadOptions
{
    /// <summary>
    /// The type of content to download (video, audio, best, worst).
    /// </summary>
    public MediaContentType ContentType { get; init; } = MediaContentType.Best;

    /// <summary>
    /// Target video height in pixels (e.g., 1080 for 1080p).
    /// Null for best available.
    /// </summary>
    public int? TargetHeight { get; init; }

    /// <summary>
    /// Target audio bitrate in kbps.
    /// Null for best available.
    /// </summary>
    public int? TargetAudioBitrate { get; init; }

    /// <summary>
    /// Preferred video codec (e.g., "h264", "h265", "av1", "vp9").
    /// </summary>
    public string? PreferredVideoCodec { get; init; }

    /// <summary>
    /// Preferred audio codec (e.g., "aac", "mp3", "opus", "vorbis").
    /// </summary>
    public string? PreferredAudioCodec { get; init; }

    /// <summary>
    /// Output directory for downloaded files.
    /// </summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// Optional filename template (platform-specific placeholders supported).
    /// </summary>
    public string? FileNameTemplate { get; init; }

    /// <summary>
    /// Whether to include subtitles if available.
    /// </summary>
    public bool IncludeSubtitles { get; init; } = false;

    /// <summary>
    /// Languages for subtitles (e.g., "en", "es", "auto" for auto-generated).
    /// </summary>
    public IReadOnlyList<string>? SubtitleLanguages { get; init; }

    /// <summary>
    /// Maximum download rate in bytes per second.
    /// Null for unlimited.
    /// </summary>
    public long? ThrottleBytesPerSecond { get; init; }

    /// <summary>
    /// Number of retry attempts for transient failures.
    /// </summary>
    public int RetryAttempts { get; init; } = 3;

    /// <summary>
    /// Proxy URL for downloads (e.g., "http://proxy.example.com:8080").
    /// </summary>
    public string? ProxyUrl { get; init; }

    /// <summary>
    /// Cookies file path for authenticated downloads.
    /// </summary>
    public string? CookiesFilePath { get; init; }

    /// <summary>
    /// Additional engine-specific options.
    /// These are passed through to the underlying download engine.
    /// </summary>
    public Dictionary<string, string>? AdditionalOptions { get; init; }
}

/// <summary>
/// Progress information for an ongoing download.
/// </summary>
public record DownloadProgress
{
    /// <summary>
    /// Current download state.
    /// </summary>
    public DownloadState State { get; init; }

    /// <summary>
    /// Bytes downloaded so far.
    /// </summary>
    public long BytesDownloaded { get; init; }

    /// <summary>
    /// Total bytes to download (null if unknown).
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>
    /// Current download speed in bytes per second.
    /// </summary>
    public double? SpeedBps { get; init; }

    /// <summary>
    /// Estimated time remaining (null if unknown).
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; init; }

    /// <summary>
    /// Progress percentage (null if TotalBytes is unknown).
    /// </summary>
    public double? Percentage => TotalBytes.HasValue && TotalBytes.Value > 0
        ? (BytesDownloaded * 100.0) / TotalBytes.Value
        : null;

    /// <summary>
    /// Human-readable status message.
    /// </summary>
    public string? StatusMessage { get; init; }
}

/// <summary>
/// Download state enumeration.
/// </summary>
public enum DownloadState
{
    /// <summary>Download is starting.</summary>
    Starting,
    /// <summary>Downloading in progress.</summary>
    Downloading,
    /// <summary>Processing/downloading is paused.</summary>
    Paused,
    /// <summary>Post-processing (e.g., muxing audio/video).</summary>
    Processing,
    /// <summary>Download completed successfully.</summary>
    Completed,
    /// <summary>Download failed.</summary>
    Failed,
    /// <summary>Download was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Result of a successful media download.
/// This is the engine-agnostic result type that all implementations must return.
/// </summary>
public record DownloadResult
{
    /// <summary>
    /// Path to the downloaded file.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// xxHash64 hash of the file content.
    /// </summary>
    public required ulong FileHash { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public required long FileSize { get; init; }

    /// <summary>
    /// Unique identifier for the media (platform-specific).
    /// </summary>
    public required string MediaId { get; init; }

    /// <summary>
    /// Platform identifier (e.g., "youtube", "vimeo", "soundcloud").
    /// </summary>
    public required string Platform { get; init; }

    /// <summary>
    /// Title of the media.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// When the media was last modified on the source platform.
    /// Used for idempotency checks.
    /// </summary>
    public DateTime? SourceLastModified { get; init; }

    /// <summary>
    /// Raw metadata from the download engine.
    /// This is engine-specific JSON that can be stored for reference.
    /// </summary>
    public string? RawMetadata { get; init; }

    /// <summary>
    /// Path to the metadata file if saved separately.
    /// </summary>
    public string? MetadataFilePath { get; init; }

    /// <summary>
    /// Paths to downloaded subtitle files.
    /// </summary>
    public IReadOnlyList<string>? SubtitleFilePaths { get; init; }

    /// <summary>
    /// Actual video height that was downloaded (null for audio-only).
    /// </summary>
    public int? ActualHeight { get; init; }

    /// <summary>
    /// Actual audio bitrate that was downloaded in kbps.
    /// </summary>
    public int? ActualAudioBitrate { get; init; }

    /// <summary>
    /// Video codec of the downloaded file.
    /// </summary>
    public string? VideoCodec { get; init; }

    /// <summary>
    /// Audio codec of the downloaded file.
    /// </summary>
    public string? AudioCodec { get; init; }

    /// <summary>
    /// Duration of the media (if available).
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// The original URL that was downloaded.
    /// </summary>
    public required string OriginalUrl { get; init; }
}

/// <summary>
/// Metadata for media fetched without downloading.
/// This is the engine-agnostic metadata type that all implementations must return.
/// </summary>
public record MediaMetadata
{
    /// <summary>
    /// Unique identifier for the media (platform-specific).
    /// </summary>
    public required string MediaId { get; init; }

    /// <summary>
    /// Platform identifier (e.g., "youtube", "vimeo", "soundcloud").
    /// </summary>
    public required string Platform { get; init; }

    /// <summary>
    /// Title of the media.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Description of the media.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// URL of the thumbnail image.
    /// </summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// Channel/uploader name.
    /// </summary>
    public string? Uploader { get; init; }

    /// <summary>
    /// Original URL of the media.
    /// </summary>
    public required string OriginalUrl { get; init; }

    /// <summary>
    /// When the media was uploaded/published.
    /// </summary>
    public DateTime? UploadDate { get; init; }

    /// <summary>
    /// When the media was last modified on the source platform.
    /// Used for idempotency checks.
    /// </summary>
    public DateTime? SourceLastModified { get; init; }

    /// <summary>
    /// Raw metadata from the download engine.
    /// This is engine-specific JSON that can be stored for reference.
    /// </summary>
    public string? RawMetadata { get; init; }

    /// <summary>
    /// Tags/categories for the media.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Expected duration of the media (if known).
    /// </summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Exception thrown when a download operation fails.
/// </summary>
public class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
    public DownloadException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when media metadata cannot be retrieved.
/// </summary>
public class MetadataException : Exception
{
    public MetadataException(string message) : base(message) { }
    public MetadataException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Service interface for downloading media from various platforms.
/// Implementations can wrap yt-dlp, youtube-dl, gallery-dl, or any other download engine.
/// 
/// This interface is intentionally generic - it doesn't expose any engine-specific types.
/// Each implementation is responsible for mapping their internal data structures to the
/// standard <see cref="MediaMetadata"/> and <see cref="DownloadResult"/> types.
/// </summary>
public interface IDownloadService
{
    /// <summary>
    /// Fetches metadata for a media URL without downloading.
    /// </summary>
    /// <param name="url">The URL of the media.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The media metadata in engine-agnostic format.</returns>
    /// <exception cref="MetadataException">Thrown when metadata cannot be retrieved.</exception>
    Task<MediaMetadata> FetchMetadataAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads media from the specified URL.
    /// </summary>
    /// <param name="url">The URL of the media to download.</param>
    /// <param name="options">Download options (null for defaults).</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The download result in engine-agnostic format.</returns>
    /// <exception cref="DownloadException">Thrown when the download fails.</exception>
    Task<DownloadResult> DownloadAsync(
        string url,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a URL is supported by this download service.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>True if the URL is supported, false otherwise.</returns>
    bool IsUrlSupported(string url);

    /// <summary>
    /// Gets the name/identifier of this download service implementation.
    /// </summary>
    string ServiceName { get; }

    /// <summary>
    /// Gets the version of the underlying download engine.
    /// </summary>
    Task<string> GetVersionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for services that can compute idempotency keys.
/// This allows the Worker to compute consistent idempotency keys
/// regardless of which download service implementation is used.
/// </summary>
public interface IIdempotencyKeyGenerator
{
    /// <summary>
    /// Computes an idempotency key for a download request.
    /// The key should be deterministic based on:
    /// - The media URL
    /// - The storage configuration
    /// - The source's last modified date (if available)
    /// 
    /// This ensures that re-downloading the same content with the same
    /// configuration results in the same idempotency key.
    /// </summary>
    /// <param name="mediaUrl">The URL of the media.</param>
    /// <param name="storageKey">The storage configuration key.</param>
    /// <param name="sourceLastModified">When the source was last modified.</param>
    /// <returns>A deterministic idempotency key.</returns>
    string ComputeIdempotencyKey(string mediaUrl, string storageKey, DateTime? sourceLastModified);
}
