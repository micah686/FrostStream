using Shared.Entities;
using Shared.Models;

namespace Shared.Storage;

/// <summary>
/// Builds standardized versioned storage paths for media artifacts.
/// Supports multiple quality variants for both video (480p, 720p, 1080p, 4K) 
/// and audio (128k, 192k, 256k, 320k), plus transcoding outputs.
/// </summary>
public static class StoragePathBuilder
{
    /// <summary>
    /// Builds a storage path for a media file based on its metadata and file hash.
    /// Format: {platform}/{mediaId}/{variantType}/{quality}/v{fileHash}/{fileHash}{extension}
    /// </summary>
    /// <param name="metadata">The media metadata containing platform and ID</param>
    /// <param name="fileHash">The hash of the file content</param>
    /// <param name="localFilePath">The local file path to extract extension from</param>
    /// <param name="mediaType">Type of media (Video or Audio)</param>
    /// <param name="quality">Quality (resolution for video, bitrate for audio)</param>
    public static string BuildMediaPath(
        YtDlpMetadata metadata,
        string fileHash,
        string localFilePath,
        MediaType mediaType,
        Quality quality)
    {
        var extension = Path.GetExtension(localFilePath);
        var qualityFolder = FormatQualityFolder(mediaType, quality);
        return $"{metadata.Platform}/{metadata.Id}/original/{qualityFolder}/v{fileHash}/{fileHash}{extension}";
    }

    /// <summary>
    /// Builds a storage path for a media file with explicit parameters.
    /// Format: {platform}/{mediaId}/{variantType}/{quality}/v{fileHash}/{fileHash}{extension}
    /// </summary>
    /// <param name="platform">The platform identifier (e.g., "youtube", "vimeo")</param>
    /// <param name="mediaId">The unique media identifier from the platform</param>
    /// <param name="fileHash">The hash of the file content</param>
    /// <param name="extension">The file extension including the dot (e.g., ".mp4", ".mp3")</param>
    /// <param name="mediaType">Type of media (Video or Audio)</param>
    /// <param name="quality">Quality (resolution for video, bitrate for audio)</param>
    /// <param name="variantType">Type of variant (Original or Transcoded)</param>
    public static string BuildMediaPath(
        string platform,
        string mediaId,
        string fileHash,
        string extension,
        MediaType mediaType,
        Quality quality,
        VideoVariantType variantType = VideoVariantType.Original)
    {
        var qualityFolder = FormatQualityFolder(mediaType, quality);
        var variantFolder = variantType.ToString().ToLowerInvariant();
        return $"{platform}/{mediaId}/{variantFolder}/{qualityFolder}/v{fileHash}/{fileHash}{extension}";
    }

    /// <summary>
    /// Builds a storage path for a transcoded variant of an existing media.
    /// Format: {platform}/{mediaId}/transcoded/{quality}/v{fileHash}/{fileHash}{extension}
    /// </summary>
    /// <param name="sourceVersion">The source version being transcoded</param>
    /// <param name="fileHash">The hash of the transcoded file content</param>
    /// <param name="extension">The file extension including the dot</param>
    /// <param name="targetQuality">Target quality for the transcoded variant</param>
    /// <param name="codec">Optional codec identifier (e.g., "h265", "av1", "opus")</param>
    public static string BuildTranscodedPath(
        VideoVersion sourceVersion,
        string fileHash,
        string extension,
        Quality targetQuality,
        string? codec = null)
    {
        // Extract platform and mediaId from the source path
        var pathParts = sourceVersion.StoragePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length < 2)
            throw new ArgumentException("Invalid source version path", nameof(sourceVersion));

        var platform = pathParts[0];
        var mediaId = pathParts[1];
        var qualityFolder = FormatQualityFolder(sourceVersion.MediaType, targetQuality);
        
        // Include codec in folder name if specified
        var hashFolder = codec != null 
            ? $"v{fileHash}_{codec}" 
            : $"v{fileHash}";

        return $"{platform}/{mediaId}/transcoded/{qualityFolder}/{hashFolder}/{fileHash}{extension}";
    }

    /// <summary>
    /// Formats quality as a folder name based on media type.
    /// - Video: "480", "720", "1080", "2160" (resolution in pixels)
    /// - Audio: "128k", "192k", "256k", "320k" (bitrate in kbps)
    /// </summary>
    private static string FormatQualityFolder(MediaType mediaType, Quality quality)
    {
        if (quality == Quality.Unknown)
            return "unknown";

        return quality switch
        {
            // Video resolutions
            Quality.P480 => "480",
            Quality.P720 => "720",
            Quality.P1080 => "1080",
            Quality.P1440 => "1440",
            Quality.P4K => "2160",
            Quality.P8K => "4320",
            // Audio bitrates
            Quality.K128 => "128k",
            Quality.K192 => "192k",
            Quality.K256 => "256k",
            Quality.K320 => "320k",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Parses a quality folder name to Quality enum value.
    /// </summary>
    public static Quality ParseQualityFolder(string qualityFolder)
    {
        return qualityFolder.ToLowerInvariant() switch
        {
            // Video resolutions
            "480" => Quality.P480,
            "720" => Quality.P720,
            "1080" => Quality.P1080,
            "1440" => Quality.P1440,
            "2160" or "4k" => Quality.P4K,
            "4320" or "8k" => Quality.P8K,
            // Audio bitrates
            "128k" => Quality.K128,
            "192k" => Quality.K192,
            "256k" => Quality.K256,
            "320k" => Quality.K320,
            _ => Quality.Unknown
        };
    }

    /// <summary>
    /// Parses a storage path to extract path components.
    /// </summary>
    /// <param name="storagePath">The storage path to parse</param>
    /// <returns>A parsed path info object, or null if parsing fails</returns>
    public static StoragePathInfo? ParsePath(string storagePath)
    {
        try
        {
            var parts = storagePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                return null;

            var platform = parts[0];
            var mediaId = parts[1];
            var variantTypeStr = parts[2];
            var qualityStr = parts[3];
            var hashFolder = parts[4];

            if (!Enum.TryParse<VideoVariantType>(variantTypeStr, ignoreCase: true, out var variantType))
                variantType = VideoVariantType.Original;

            var quality = ParseQualityFolder(qualityStr);

            // Extract file hash from hash folder (removes 'v' prefix and optional codec suffix)
            var fileHash = hashFolder.StartsWith('v') 
                ? hashFolder[1..].Split('_')[0] 
                : hashFolder;

            return new StoragePathInfo(platform, mediaId, variantType, quality, fileHash);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the parent directory path for all variants of a specific media.
    /// Format: {platform}/{mediaId}/
    /// </summary>
    public static string GetMediaBasePath(string platform, string mediaId)
    {
        return $"{platform}/{mediaId}/";
    }

    /// <summary>
    /// Gets the directory path for a specific variant type of a media.
    /// Format: {platform}/{mediaId}/{variantType}/
    /// </summary>
    public static string GetVariantTypePath(string platform, string mediaId, VideoVariantType variantType)
    {
        return $"{platform}/{mediaId}/{variantType.ToString().ToLowerInvariant()}/";
    }

    #region Backwards Compatibility

    /// <summary>
    /// Builds a storage path for a video file based on its metadata and file hash.
    /// Format: {platform}/{videoId}/original/v{fileHash}/{fileHash}{extension}
    /// </summary>
    [Obsolete("Use BuildMediaPath with MediaType parameter for proper versioned storage")]
    public static string BuildVideoPath(YtDlpMetadata metadata, string fileHash, string localFilePath)
    {
        return BuildMediaPath(metadata, fileHash, localFilePath, MediaType.Video, Quality.Unknown);
    }

    /// <summary>
    /// Builds a storage path for a video file with quality variant support.
    /// Format: {platform}/{videoId}/original/{quality}/v{fileHash}/{fileHash}{extension}
    /// </summary>
    [Obsolete("Use BuildMediaPath with MediaType parameter for proper versioned storage")]
    public static string BuildVideoPath(YtDlpMetadata metadata, string fileHash, string localFilePath, Quality quality)
    {
        return BuildMediaPath(metadata, fileHash, localFilePath, MediaType.Video, quality);
    }

    /// <summary>
    /// Builds a storage path for a video file with explicit parameters.
    /// Format: {platform}/{videoId}/{variantType}/{quality}/v{fileHash}/{fileHash}{extension}
    /// </summary>
    [Obsolete("Use BuildMediaPath with MediaType parameter for proper versioned storage")]
    public static string BuildVideoPath(string platform, string videoId, string fileHash, string extension)
    {
        return BuildMediaPath(platform, videoId, fileHash, extension, MediaType.Video, Quality.Unknown, VideoVariantType.Original);
    }

    /// <summary>
    /// Builds a storage path for a video file with full variant support.
    /// Format: {platform}/{videoId}/{variantType}/{quality}/v{fileHash}/{fileHash}{extension}
    /// </summary>
    [Obsolete("Use BuildMediaPath with MediaType parameter for proper versioned storage")]
    public static string BuildVideoPath(
        string platform,
        string videoId,
        string fileHash,
        string extension,
        Quality quality,
        VideoVariantType variantType = VideoVariantType.Original)
    {
        return BuildMediaPath(platform, videoId, fileHash, extension, MediaType.Video, quality, variantType);
    }

    /// <summary>
    /// Gets the parent directory path for all variants of a specific video.
    /// Format: {platform}/{videoId}/
    /// </summary>
    [Obsolete("Use GetMediaBasePath instead")]
    public static string GetVideoBasePath(string platform, string videoId)
    {
        return GetMediaBasePath(platform, videoId);
    }

    #endregion
}

/// <summary>
/// Contains parsed information from a storage path.
/// </summary>
public record StoragePathInfo(
    string Platform,
    string MediaId,
    VideoVariantType VariantType,
    Quality Quality,
    string FileHash);
