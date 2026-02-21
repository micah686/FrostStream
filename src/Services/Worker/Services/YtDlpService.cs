using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    /// Runs: yt-dlp --dump-json {url}
    /// </summary>
    public async Task<YtDlpMetadata> FetchMetadataAsync(string videoUrl, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching metadata for {Url}", videoUrl);

        var (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", $"--dump-json \"{videoUrl}\"", ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"yt-dlp --dump-json failed (exit {exitCode}): {stderr}");

        return ParseMetadata(stdout);
    }

    /// <summary>
    /// Downloads a video to a local directory and returns the result with file hash.
    /// </summary>
    public async Task<YtDlpDownloadResult> DownloadAsync(string videoUrl, string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        // First fetch metadata
        var metadata = await FetchMetadataAsync(videoUrl, ct);

        // Download the video file
        var outputTemplate = Path.Combine(outputDir, "%(id)s.%(ext)s");
        _logger.LogInformation("Downloading video {Id} to {OutputDir}", metadata.Id, outputDir);

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            "yt-dlp",
            $"-o \"{outputTemplate}\" --no-playlist \"{videoUrl}\"",
            ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"yt-dlp download failed (exit {exitCode}): {stderr}");

        // Find the downloaded file (yt-dlp may choose different extensions)
        var downloadedFile = Directory.GetFiles(outputDir)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == metadata.Id);

        if (downloadedFile == null)
        {
            // Fallback: pick the first (and likely only) file
            downloadedFile = Directory.GetFiles(outputDir).FirstOrDefault();
        }

        if (downloadedFile == null)
            throw new FileNotFoundException($"No downloaded file found in {outputDir}");

        _logger.LogInformation("Downloaded file: {FilePath}", downloadedFile);

        // Compute SHA256 hash
        var fileHash = await ComputeFileHashAsync(downloadedFile, ct);

        return new YtDlpDownloadResult(metadata, downloadedFile, fileHash);
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

    private static YtDlpMetadata ParseMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing 'id' in yt-dlp output");

        // yt-dlp uses "extractor_key" or "extractor" for the platform
        var platform = root.TryGetProperty("extractor_key", out var extractorKey)
            ? extractorKey.GetString()?.ToLowerInvariant() ?? "unknown"
            : root.TryGetProperty("extractor", out var extractor)
                ? extractor.GetString()?.ToLowerInvariant() ?? "unknown"
                : "unknown";

        var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "untitled" : "untitled";

        // yt-dlp may provide "modified_date" or "upload_date"
        DateTime? sourceLastModified = null;
        if (root.TryGetProperty("modified_date", out var modDate) && modDate.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParseExact(modDate.GetString(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsed))
                sourceLastModified = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        else if (root.TryGetProperty("upload_date", out var uploadDate) && uploadDate.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParseExact(uploadDate.GetString(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsed))
                sourceLastModified = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return new YtDlpMetadata(id, platform, title, sourceLastModified, json);
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        _logger.LogDebug("Running: {FileName} {Arguments}", fileName, arguments);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        _logger.LogDebug("Process exited with code {ExitCode}", process.ExitCode);

        return (process.ExitCode, stdout, stderr);
    }
}
