using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    IOptions<PotProviderOptions> potOptions,
    ILogger<StartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Only provision the bgutil POT plugin when POT is enabled — otherwise its release download
        // would become an unnecessary startup dependency that could block the host.
        var downloadPotPlugin = potOptions.Value.Enabled;

        logger.LogInformation(
            "Provisioning yt-dlp/ffmpeg/ffprobe binaries{PluginSuffix}...",
            downloadPotPlugin ? " and the bgutil POT plugin" : string.Empty);

        var result = await downloader.DownloadAllAsync(
            new BinaryDownloadOptions
            {
                SkipExisting = true,
                DownloadYtDlp = true,
                DownloadFfmpeg = true,
                DownloadFfprobe = true,
                DownloadDeno = true,
                DownloadBgUtilPlugin = downloadPotPlugin,
            },
            progress: null,
            ct: cancellationToken);

        logger.LogInformation(
            "Binaries ready: yt-dlp={YtDlpPath} ffmpeg={FfmpegPath} ffprobe={FfprobePath} potPlugin={PotPluginDir}",
            result.YtDlpPath, result.FfmpegPath, result.FfprobePath, result.BgUtilPluginDir);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
