using System.IO.Hashing;
using Shared.Entities;
using Shared.Models;

namespace Shared.Storage;

/// <summary>
/// Builds standardized versioned storage paths for media artifacts.
/// Format: {platform}/{mediaId}/v{version}/{fileHash}{extension}
/// </summary>
public static class StoragePathBuilder
{
    /// <summary>
    /// Builds a storage path for a media file.
    /// Format: {platform}/{mediaId}/v{version}/{fileHash}{extension}
    /// </summary>
    /// <param name="platform">The platform identifier (e.g., "youtube", "vimeo")</param>
    /// <param name="mediaId">The unique media identifier from the platform</param>
    /// <param name="fileHash">The hash of the file content</param>
    /// <param name="extension">The file extension including the dot (e.g., ".mp4", ".mp3")</param>
    /// <param name="version">The version number (integer, starting from 1)</param>
    public static string BuildMediaPath(
        string platform,
        string mediaId,
        string originalUrl,
        string fileHash,
        string extension,
        int version)
    {
        {
            var hashedUrl = XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(originalUrl));
            return $"{platform}/{hashedUrl}/v{version}/{mediaId}_{fileHash}{extension}";
        }
    }

    /// <summary>
    /// Builds a storage path for a transcoded variant of an existing media.
    /// Format: {platform}/{mediaId}/v{version}/{fileHash}{extension}
    /// </summary>
    /// <param name="sourceVersion">The source version being transcoded</param>
    /// <param name="fileHash">The hash of the transcoded file content</param>
    /// <param name="extension">The file extension including the dot</param>
    /// <param name="targetVersion">The target version number for the transcoded variant</param>
    public static string BuildTranscodedPath(
        VideoVersion sourceVersion,
        string fileHash,
        string extension,
        int targetVersion)
    {
        // Extract platform and mediaId from the source path
        var pathParts = sourceVersion.StoragePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length < 2)
            throw new ArgumentException("Invalid source version path", nameof(sourceVersion));

        var platform = pathParts[0];
        var mediaId = pathParts[1];

        return $"{platform}/{mediaId}/v{targetVersion}/{fileHash}{extension}";
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
            if (parts.Length < 3)
                return null;

            var platform = parts[0];
            var mediaId = parts[1];
            var versionFolder = parts[2];

            // Extract version number from folder (removes 'v' prefix)
            if (!versionFolder.StartsWith('v') || !int.TryParse(versionFolder[1..], out var versionNum))
                return null;

            // Extract file hash from filename (without extension)
            var fileName = parts.Length > 3 ? parts[^1] : Path.GetFileName(storagePath);
            var fileHash = Path.GetFileNameWithoutExtension(fileName);

            return new StoragePathInfo(platform, mediaId, versionNum, fileHash);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Contains parsed information from a storage path.
/// </summary>
public record StoragePathInfo(
    string Platform,
    string MediaId,
    int VersionNum,
    string FileHash);
