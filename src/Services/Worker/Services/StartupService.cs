using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YtDlpSharpLib.Provisioning;

namespace Worker.Services;

/// <summary>
/// Plain <see cref="IHostedService"/> (not <see cref="BackgroundService"/>) so its
/// <see cref="StartAsync"/> blocks the host startup until yt-dlp/ffmpeg/ffprobe are on disk.
/// Registered before <see cref="DownloadCommandsConsumerService"/> so consumers cannot pull
/// commands until the binaries are ready.
/// </summary>
public sealed class StartupService(
    IYtDlpBinaryDownloader downloader,
    ILogger<StartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Provisioning yt-dlp/ffmpeg/ffprobe binaries...");

        var result = await downloader.DownloadAllAsync(
            new BinaryDownloadOptions
            {
                SkipExisting = true,
                DownloadYtDlp = true,
                DownloadFfmpeg = true,
                DownloadFfprobe = true,
                DownloadDeno = false,
            },
            progress: null,
            ct: cancellationToken);

        logger.LogInformation(
            "Binaries ready: yt-dlp={YtDlpPath} ffmpeg={FfmpegPath} ffprobe={FfprobePath}",
            result.YtDlpPath, result.FfmpegPath, result.FfprobePath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
