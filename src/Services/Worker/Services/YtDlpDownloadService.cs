using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
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
/// Implementation of <see cref="IDownloadService"/> and <see cref="IIdempotencyKeyGenerator"/>
/// that wraps yt-dlp via YoutubeDLSharp.
/// </summary>
public class YtDlpDownloadService : IDownloadService, IIdempotencyKeyGenerator
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

            // Map yt-dlp specific type to generic MediaMetadata
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

            // Note: Bandwidth throttling and additional options would need
            // to be passed through CustomOptions if the library supports it.
            // For now, these are placeholders for future implementation.

            // Start progress reporting
            if (progress != null)
            {
                progress.Report(new DownloadProgress
                {
                    State = DownloadState.Starting,
                    BytesDownloaded = 0,
                    StatusMessage = "Starting download..."
                });
            }

            // Execute download
            var result = await ytDl.RunVideoDownload(url,
                overrideOptions: optionSet,
                ct: cancellationToken);

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
                await ParseDownloadedMetadataAsync(outputDir, metadata.MediaId, cancellationToken);

            // Report completion
            progress?.Report(new DownloadProgress
            {
                State = DownloadState.Completed,
                BytesDownloaded = fileInfo.Length,
                TotalBytes = fileInfo.Length,
                StatusMessage = "Download completed"
            });

            // Map to generic DownloadResult
            return new DownloadResult
            {
                FilePath = filePath,
                FileHash = fileHash,
                FileSize = fileInfo.Length,
                MediaId = metadata.MediaId,
                Platform = metadata.Platform,
                Title = metadata.Title,
                SourceLastModified = metadata.SourceLastModified,
                RawMetadata = metadata.RawMetadata,
                MetadataFilePath = Path.Combine(outputDir, $"{metadata.MediaId}.info.json"),
                ActualHeight = actualHeight,
                ActualAudioBitrate = actualBitrate,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                Duration = duration ?? metadata.Duration,
                OriginalUrl = url
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
        return Task.FromResult(_ytDlpPath);
    }

    /// <inheritdoc />
    public string ComputeIdempotencyKey(string mediaUrl, string storageKey, DateTime? sourceLastModified)
    {
        var input = $"{mediaUrl}|{storageKey}|{sourceLastModified?.ToString("O") ?? "null"}";
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(input)).ToString();
    }

    private YoutubeDL CreateYoutubeDL()
    {
        return new YoutubeDL
        {
            YoutubeDLPath = _ytDlpPath
        };
    }

    /// <summary>
    /// Maps yt-dlp specific VideoData to generic MediaMetadata.
    /// This is where the engine-specific to generic mapping happens.
    /// </summary>
    private static MediaMetadata MapToMediaMetadata(VideoData data, string originalUrl)
    {
        var sourceLastModified = data.ModifiedTimestamp ?? data.UploadDate;
        if (sourceLastModified.HasValue)
            sourceLastModified = DateTime.SpecifyKind(sourceLastModified.Value, DateTimeKind.Utc);

        // Try to extract duration
        TimeSpan? duration = null;
        if (data.Duration.HasValue && data.Duration.Value > 0)
        {
            duration = TimeSpan.FromSeconds(data.Duration.Value);
        }

        return new MediaMetadata
        {
            MediaId = data.ID ?? throw new MetadataException("yt-dlp returned no video ID"),
            Platform = (data.ExtractorKey ?? data.Extractor ?? "unknown").ToLowerInvariant(),
            Title = data.Title ?? "untitled",
            Description = data.Description,
            ThumbnailUrl = data.Thumbnail,
            Uploader = data.Uploader ?? data.Channel,
            OriginalUrl = originalUrl,
            UploadDate = data.UploadDate,
            SourceLastModified = sourceLastModified,
            RawMetadata = JsonConvert.SerializeObject(data),
            Tags = data.Tags?.ToList(),
            Duration = duration
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
        ParseDownloadedMetadataAsync(string outputDir, string mediaId, CancellationToken cancellationToken)
    {
        try
        {
            var infoJsonPath = Path.Combine(outputDir, $"{mediaId}.info.json");
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
            _logger.LogDebug(ex, "Failed to parse downloaded metadata for {MediaId}", mediaId);
            return (null, null, null, null, null);
        }
    }

    private static async Task<ulong> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        var xxHash = new XxHash64();
        await using var stream = File.OpenRead(filePath);
        await xxHash.AppendAsync(stream, cancellationToken);
        return xxHash.GetCurrentHashAsUInt64();
    }
}
