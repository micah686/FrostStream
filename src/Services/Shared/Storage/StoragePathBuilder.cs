using Shared.Models;

namespace Shared.Storage;

/// <summary>
/// Builds standardized storage paths for video artifacts.
/// </summary>
public static class StoragePathBuilder
{
    /// <summary>
    /// Builds a storage path for a video file based on its metadata and file hash.
    /// Format: {platform}/{videoId}/v{fileHash}{extension}
    /// </summary>
    /// <param name="metadata">The video metadata containing platform and ID</param>
    /// <param name="fileHash">The hash of the file content</param>
    /// <param name="localFilePath">The local file path to extract extension from</param>
    public static string BuildVideoPath(YtDlpMetadata metadata, string fileHash, string localFilePath)
    {
        var extension = Path.GetExtension(localFilePath);
        return $"{metadata.Platform}/{metadata.Id}/v{fileHash}{extension}";
    }

    /// <summary>
    /// Builds a storage path for a video file with explicit parameters.
    /// Format: {platform}/{videoId}/v{fileHash}{extension}
    /// </summary>
    /// <param name="platform">The platform identifier (e.g., "youtube", "vimeo")</param>
    /// <param name="videoId">The unique video identifier from the platform</param>
    /// <param name="fileHash">The hash of the file content</param>
    /// <param name="extension">The file extension including the dot (e.g., ".mp4")</param>
    public static string BuildVideoPath(string platform, string videoId, string fileHash, string extension)
    {
        return $"{platform}/{videoId}/v{fileHash}{extension}";
    }
}
