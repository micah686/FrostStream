using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Download;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using DownloadProgress = Shared.Download.DownloadProgress;
using DownloadState = Shared.Download.DownloadState;

namespace Worker.Services;

/// <summary>
/// Implementation of <see cref="IDownloadService"/> that wraps yt-dlp via YoutubeDLSharp.
/// </summary>
public class YtDlpDownloadService : IDownloadService
{
    private readonly ILogger<YtDlpDownloadService> _logger;
    private readonly string _ytDlpPath;

    public string ServiceName => "yt-dlp";

    public YtDlpDownloadService(ILogger<YtDlpDownloadService> logger, string? ytDlpPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ytDlpPath = ytDlpPath ?? "yt-dlp"; // Assume in PATH if not specified
    }

    /// <inheritdoc />
    public async Task<MediaMetadata> FetchMetadataAsync(string url, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching metadata for {Url}", url);

        try
        {
            var ytDl = CreateYoutubeDL();
            var result = await ytDl.RunVideoDataFetch(url,
                overrideOptions: new OptionSet { NoPlaylist = true },
                ct: cancellationToken);

            if (!result.Success)
            {
                var errorMsg = string.Join("; ", result.ErrorOutput);
                _logger.LogError("yt-dlp metadata fetch failed: {Error}", errorMsg);
                throw new MetadataException($"yt-dlp metadata fetch failed: {errorMsg}");
            }

            return MapToMediaMetadata(result.Data, url);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching metadata for {Url}", url);
            throw new MetadataException($"Failed to fetch metadata: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<DownloadResult> DownloadAsync(
        string url,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DownloadOptions();
        
        var outputDir = options.OutputDirectory ?? Path.Combine(Path.GetTempPath(), "froststream", "downloads");
        Directory.CreateDirectory(outputDir);

        _logger.LogInformation(
            "Starting download for {Url} to {OutputDir} (ContentType: {ContentType})",
            url, outputDir, options.ContentType);

        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            // Fetch metadata first
            var metadata = await FetchMetadataAsync(url, cancellationToken);
            
            // Build format selection string based on options
            var formatSelector = BuildFormatSelector(options);
            
            var ytDl = CreateYoutubeDL();
            ytDl.OutputFolder = outputDir;
            ytDl.OutputFileTemplate = options.FileNameTemplate ?? "%(id)s.%(ext)s";

            // Build option set
            var optionSet = new OptionSet
            {
                NoPlaylist = true,
                Format = formatSelector,
            };

            // Apply throttling if specified
            // Note: Bandwidth throttling would require custom command line args
            // options.ThrottleBytesPerSecond can be used with AdditionalOptions

            // Apply proxy if specified
            if (!string.IsNullOrEmpty(options.ProxyUrl))
            {
                optionSet.Proxy = options.ProxyUrl;
            }

            // Apply cookies if specified
            if (!string.IsNullOrEmpty(options.CookiesFilePath))
            {
                optionSet.Cookies = options.CookiesFilePath;
            }

            // Note: AdditionalOptions support would need investigation of CustomOptions API

            // Start progress polling if callback provided
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? progressTask = null;
            
            if (progress != null)
            {
                progress.Report(new DownloadProgress
                {
                    State = DownloadState.Starting,
                    BytesDownloaded = 0,
                    StatusMessage = "Starting download..."
                });
                
                progressTask = RunProgressPollingAsync(
                    outputDir, metadata.Id, metadata.Platform, progress, progressCts.Token);
            }

            // Execute download
            var result = await ytDl.RunVideoDownload(url,
                overrideOptions: optionSet,
                ct: cancellationToken);

            // Stop progress polling
            progressCts.Cancel();
            if (progressTask != null)
            {
                try { await progressTask; } catch (OperationCanceledException) { }
            }

            if (!result.Success)
            {
                var errorMsg = string.Join("; ", result.ErrorOutput);
                _logger.LogError("yt-dlp download failed: {Error}", errorMsg);
                throw new DownloadException($"yt-dlp download failed: {errorMsg}");
            }

            var filePath = result.Data
                ?? throw new DownloadException("yt-dlp produced no output file");

            _logger.LogInformation("Downloaded file: {FilePath} in {ElapsedMs}ms", 
                filePath, stopwatch.ElapsedMilliseconds);

            // Compute file hash and get details
            var fileInfo = new FileInfo(filePath);
            var fileHash = await ComputeFileHashAsync(filePath, cancellationToken);
            
            // Parse info.json for detailed metadata
            var (actualHeight, actualBitrate, videoCodec, audioCodec, duration) = 
                await ParseDownloadedMetadataAsync(outputDir, metadata.Id, cancellationToken);

            // Report completion
            progress?.Report(new DownloadProgress
            {
                State = DownloadState.Completed,
                BytesDownloaded = fileInfo.Length,
                TotalBytes = fileInfo.Length,
                StatusMessage = "Download completed"
            });

            return new DownloadResult
            {
                FilePath = filePath,
                FileHash = fileHash,
                FileSize = fileInfo.Length,
                Metadata = metadata,
                MetadataFilePath = Path.Combine(outputDir, $"{metadata.Id}.info.json"),
                ActualHeight = actualHeight,
                ActualAudioBitrate = actualBitrate,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                Duration = duration
            };
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new DownloadProgress
            {
                State = DownloadState.Cancelled,
                StatusMessage = "Download cancelled"
            });
            throw;
        }
        catch (DownloadException)
        {
            progress?.Report(new DownloadProgress
            {
                State = DownloadState.Failed,
                StatusMessage = "Download failed"
            });
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading {Url}", url);
            progress?.Report(new DownloadProgress
            {
                State = DownloadState.Failed,
                StatusMessage = $"Download failed: {ex.Message}"
            });
            throw new DownloadException($"Download failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public bool IsUrlSupported(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        // yt-dlp supports many platforms - do basic URL validation
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <inheritdoc />
    public Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        // yt-dlp version is determined by the binary path/version installed
        // This could be enhanced to actually run "yt-dlp --version" if needed
        return Task.FromResult(_ytDlpPath);
    }

    private YoutubeDL CreateYoutubeDL()
    {
        return new YoutubeDL
        {
            YoutubeDLPath = _ytDlpPath
        };
    }

    private static MediaMetadata MapToMediaMetadata(VideoData data, string originalUrl)
    {
        var sourceLastModified = data.ModifiedTimestamp ?? data.UploadDate;
        if (sourceLastModified.HasValue)
            sourceLastModified = DateTime.SpecifyKind(sourceLastModified.Value, DateTimeKind.Utc);

        return new MediaMetadata
        {
            Id = data.ID ?? throw new MetadataException("yt-dlp returned no video ID"),
            Platform = (data.ExtractorKey ?? data.Extractor ?? "unknown").ToLowerInvariant(),
            Title = data.Title ?? "untitled",
            Description = data.Description,
            ThumbnailUrl = data.Thumbnail,
            Uploader = data.Uploader ?? data.Channel,
            OriginalUrl = originalUrl,
            UploadDate = data.UploadDate,
            SourceLastModified = sourceLastModified,
            RawJson = JsonConvert.SerializeObject(data),
            Tags = data.Tags?.ToList()
        };
    }

    private static string BuildFormatSelector(DownloadOptions options)
    {
        var selectors = new List<string>();

        // Base format selection based on content type
        switch (options.ContentType)
        {
            case MediaContentType.Audio:
                selectors.Add("bestaudio/best");
                break;
            case MediaContentType.Video:
                selectors.Add("bestvideo*+bestaudio/best");
                break;
            case MediaContentType.Best:
                selectors.Add("best*");
                break;
            case MediaContentType.Worst:
                selectors.Add("worst");
                break;
        }

        // Add height filter if specified
        if (options.TargetHeight.HasValue && options.ContentType != MediaContentType.Audio)
        {
            var heightSelector = $"best[height<={options.TargetHeight.Value}]";
            selectors.Insert(0, heightSelector);
        }

        return string.Join("/", selectors);
    }

    private async Task<(int? Height, int? Bitrate, string? VideoCodec, string? AudioCodec, TimeSpan? Duration)> 
        ParseDownloadedMetadataAsync(string outputDir, string videoId, CancellationToken cancellationToken)
    {
        try
        {
            var infoJsonPath = Path.Combine(outputDir, $"{videoId}.info.json");
            if (!File.Exists(infoJsonPath))
                return (null, null, null, null, null);

            var json = await File.ReadAllTextAsync(infoJsonPath, cancellationToken);
            var data = JObject.Parse(json);

            var height = data["height"]?.Value<int?>();
            var bitrate = data["abr"]?.Value<int?>(); // Audio bitrate in kbps
            var vcodec = data["vcodec"]?.Value<string>();
            var acodec = data["acodec"]?.Value<string>();
            var durationSecs = data["duration"]?.Value<double?>();

            TimeSpan? duration = durationSecs.HasValue 
                ? TimeSpan.FromSeconds(durationSecs.Value) 
                : null;

            return (height, bitrate, vcodec, acodec, duration);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse downloaded metadata for {VideoId}", videoId);
            return (null, null, null, null, null);
        }
    }

    private async Task RunProgressPollingAsync(
        string outputDir,
        string videoId,
        string platform,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            long lastReportedSize = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Look for partial/downloading files
                var files = Directory.GetFiles(outputDir, $"{videoId}*", SearchOption.TopDirectoryOnly);
                
                var targetFile = files
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
                        progress.Report(new DownloadProgress
                        {
                            State = DownloadState.Downloading,
                            BytesDownloaded = currentSize,
                            StatusMessage = $"Downloading {platform} content..."
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when download completes
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Progress polling encountered an error");
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    public static string ComputeIdempotencyKey(string videoUrl, string storageKey, DateTime? sourceLastModified)
    {
        var input = $"{videoUrl}|{storageKey}|{sourceLastModified?.ToString("O") ?? "null"}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }
}
