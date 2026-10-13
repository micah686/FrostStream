using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YtDlpSharpLib.Provisioning;

namespace Worker.Services;

/// <summary>Refreshes the media toolset from its release sources at a configurable interval.</summary>
public sealed class MediaToolsUpdateManager
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IYtDlpBinaryDownloader downloader;
    private readonly IConfiguration configuration;
    private readonly ILogger<MediaToolsUpdateManager> logger;
    private readonly string toolsDirectory;

    public MediaToolsUpdateManager(IYtDlpBinaryDownloader downloader, IConfiguration configuration,
        ILogger<MediaToolsUpdateManager> logger)
        : this(downloader, configuration, logger, Path.Combine(AppContext.BaseDirectory, "tools")) { }

    internal MediaToolsUpdateManager(IYtDlpBinaryDownloader downloader, IConfiguration configuration,
        ILogger<MediaToolsUpdateManager> logger, string toolsDirectory)
    {
        this.downloader = downloader;
        this.configuration = configuration;
        this.logger = logger;
        this.toolsDirectory = toolsDirectory;
    }

    public async Task RefreshIfDueAsync(CancellationToken cancellationToken)
    {
        var refreshDays = configuration.GetValue("MediaTools:RefreshIntervalDays", 7);
        if (refreshDays <= 0)
            return;

        await gate.WaitAsync(cancellationToken);
        try
        {
            var marker = Path.Combine(toolsDirectory, ".last-media-tools-refresh");
            if (File.Exists(marker) &&
                DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(refreshDays))
                return;

            try
            {
                logger.LogInformation("Refreshing media tools from their release sources");
                // Lite links FFmpeg to the distro packages installed in its image. The
                // ffbinaries release can be older than those packages; retain the links.
                var ffmpegManagedByImage = new FileInfo(Path.Combine(toolsDirectory, YtDlpPaths.FfmpegFileName)).LinkTarget is not null;
                var ffprobeManagedByImage = new FileInfo(Path.Combine(toolsDirectory, YtDlpPaths.FfprobeFileName)).LinkTarget is not null;
                await downloader.DownloadAllAsync(new BinaryDownloadOptions
                {
                    Directory = toolsDirectory,
                    SkipExisting = false,
                    DownloadYtDlp = true,
                    DownloadFfmpeg = !ffmpegManagedByImage,
                    DownloadFfprobe = !ffprobeManagedByImage,
                    DownloadDeno = true,
                    DownloadBgUtilPlugin = false
                }, ct: cancellationToken);
                await File.WriteAllTextAsync(marker,
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
                logger.LogInformation("Media tools refreshed successfully");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Startup has already ensured usable files exist. Keep those files and retry later.
                logger.LogWarning(ex, "Media tool refresh failed; keeping installed binaries");
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class MediaToolsRefreshService(MediaToolsUpdateManager updates) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await updates.RefreshIfDueAsync(stoppingToken);
    }
}
