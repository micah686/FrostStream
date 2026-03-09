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
    string FileHash,
    long FileSize);

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
    public async Task<YtDlpDownloadResult> DownloadAsync(
        string videoUrl,
        string outputDir,
        CancellationToken ct = default)
    {
        return await DownloadAsync(videoUrl, outputDir, progressCallback: null, ct);
    }

    /// <summary>
    /// Downloads a video to a local directory with progress callbacks.
    /// </summary>
    public async Task<YtDlpDownloadResult> DownloadAsync(
        string videoUrl,
        string outputDir,
        Func<long, long?, Task>? progressCallback,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var metadata = await FetchMetadataAsync(videoUrl, ct);

        _logger.LogInformation("Downloading video {Id} to {OutputDir}", metadata.Id, outputDir);

        // Start progress reporting if callback provided
        using var progressCts = new CancellationTokenSource();
        Task? progressTask = null;

        var ytDl = new YoutubeDL
        {
            OutputFolder = outputDir,
            OutputFileTemplate = "%(id)s.%(ext)s"
        };

        // If we have a progress callback, start a polling task to report file size changes
        if (progressCallback != null)
        {
            progressTask = RunProgressPollingAsync(outputDir, metadata.Id, progressCallback, progressCts.Token);
        }

        try
        {
            var result = await ytDl.RunVideoDownload(videoUrl,
                overrideOptions: new OptionSet { NoPlaylist = true },
                ct: ct);

            if (!result.Success)
                throw new InvalidOperationException(
                    $"yt-dlp download failed: {string.Join("; ", result.ErrorOutput)}");

            var filePath = result.Data
                ?? throw new FileNotFoundException($"yt-dlp produced no output file for {videoUrl}");

            _logger.LogInformation("Downloaded file: {FilePath}", filePath);

            // Stop progress polling
            progressCts.Cancel();
            if (progressTask != null)
            {
                try { await progressTask; }
                catch (OperationCanceledException) { }
            }

            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;
            var fileHash = await ComputeFileHashAsync(filePath, ct);

            // Report final progress
            if (progressCallback != null)
            {
                await progressCallback(fileSize, fileSize);
            }

            return new YtDlpDownloadResult(metadata, filePath, fileHash, fileSize);
        }
        catch
        {
            progressCts.Cancel();
            if (progressTask != null)
            {
                try { await progressTask; }
                catch (OperationCanceledException) { }
            }
            throw;
        }
    }

    /// <summary>
    /// Polls the output directory to report download progress based on file size.
    /// This is a best-effort approach since yt-dlp doesn't expose direct progress callbacks.
    /// </summary>
    private async Task RunProgressPollingAsync(
        string outputDir,
        string videoId,
        Func<long, long?, Task> progressCallback,
        CancellationToken ct)
    {
        try
        {
            long lastReportedSize = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

            while (await timer.WaitForNextTickAsync(ct))
            {
                // Look for the partial/downloading file
                var partialFiles = Directory.GetFiles(outputDir, $"{videoId}*", SearchOption.TopDirectoryOnly);
                var targetFile = partialFiles
                    .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();

                if (targetFile != null)
                {
                    var currentSize = targetFile.Length;
                    if (currentSize > lastReportedSize)
                    {
                        lastReportedSize = currentSize;
                        // Total size is unknown during download
                        await progressCallback(currentSize, null);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when download completes
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Progress polling encountered an error");
        }
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
