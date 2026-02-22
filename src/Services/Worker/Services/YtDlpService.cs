using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace Worker.Services;

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
    string FileHash);

/// <summary>
/// Wraps the yt-dlp command-line executable for fetching metadata and downloading videos.
/// Assumes yt-dlp is available in the system PATH.
/// </summary>
public class YtDlpService
{
    private readonly ILogger<YtDlpService> _logger;

    public YtDlpService(ILogger<YtDlpService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Fetches metadata for a video URL without downloading.
    /// </summary>
    public async Task<YtDlpMetadata> FetchMetadataAsync(string videoUrl, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching metadata for {Url}", videoUrl);

        var ytDl = new YoutubeDL();
        var result = await ytDl.RunVideoDataFetch(videoUrl,
            overrideOptions: new OptionSet { NoPlaylist = true },
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException(
                $"yt-dlp metadata fetch failed: {string.Join("; ", result.ErrorOutput)}");

        return MapToMetadata(result.Data);
    }

    /// <summary>
    /// Downloads a video to a local directory and returns the result with file hash.
    /// </summary>
    public async Task<YtDlpDownloadResult> DownloadAsync(string videoUrl, string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var metadata = await FetchMetadataAsync(videoUrl, ct);

        _logger.LogInformation("Downloading video {Id} to {OutputDir}", metadata.Id, outputDir);

        var ytDl = new YoutubeDL
        {
            OutputFolder = outputDir,
            OutputFileTemplate = "%(id)s.%(ext)s"
        };

        var result = await ytDl.RunVideoDownload(videoUrl,
            overrideOptions: new OptionSet { NoPlaylist = true },
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException(
                $"yt-dlp download failed: {string.Join("; ", result.ErrorOutput)}");

        var filePath = result.Data
            ?? throw new FileNotFoundException($"yt-dlp produced no output file for {videoUrl}");

        _logger.LogInformation("Downloaded file: {FilePath}", filePath);

        var fileHash = await ComputeFileHashAsync(filePath, ct);
        return new YtDlpDownloadResult(metadata, filePath, fileHash);
    }

    /// <summary>
    /// Computes the idempotency key: SHA256(VideoUrl + StorageKey + SourceLastModified).
    /// </summary>
    public static string ComputeIdempotencyKey(string videoUrl, string storageKey, DateTime? sourceLastModified)
    {
        var input = $"{videoUrl}|{storageKey}|{sourceLastModified?.ToString("O") ?? "null"}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    private static YtDlpMetadata MapToMetadata(VideoData data)
    {
        var sourceLastModified = data.ModifiedTimestamp ?? data.UploadDate;
        if (sourceLastModified.HasValue)
            sourceLastModified = DateTime.SpecifyKind(sourceLastModified.Value, DateTimeKind.Utc);

        var rawJson = JsonConvert.SerializeObject(data);

        return new YtDlpMetadata(
            Id:                 data.ID ?? throw new InvalidOperationException("yt-dlp returned no video ID"),
            Platform:           (data.ExtractorKey ?? data.Extractor ?? "unknown").ToLowerInvariant(),
            Title:              data.Title ?? "untitled",
            SourceLastModified: sourceLastModified,
            RawJson:            rawJson);
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}
