using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Worker.Services;
using YtDlpSharpLib.Provisioning;

namespace UnitTests.Worker;

public sealed class MediaToolsUpdateManagerTests
{
    [Test]
    public async Task Refreshes_due_binaries_once_and_marks_success()
    {
        var directory = Path.Combine(Path.GetTempPath(), "media-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var downloader = Substitute.For<IYtDlpBinaryDownloader>();
            downloader.DownloadAllAsync(Arg.Any<BinaryDownloadOptions>(), Arg.Any<IProgress<BinaryDownloadProgress>?>(), Arg.Any<CancellationToken>())
                .Returns(new BinaryDownloadResult());
            var config = new ConfigurationBuilder().Build();
            var manager = new MediaToolsUpdateManager(downloader, config,
                NullLogger<MediaToolsUpdateManager>.Instance, directory);

            await manager.RefreshIfDueAsync(default);
            await manager.RefreshIfDueAsync(default);

            await downloader.Received(1).DownloadAllAsync(
                Arg.Is<BinaryDownloadOptions>(options => !options.SkipExisting &&
                    options.DownloadYtDlp && options.DownloadDeno && options.DownloadFfmpeg && options.DownloadFfprobe),
                Arg.Any<IProgress<BinaryDownloadProgress>?>(), Arg.Any<CancellationToken>());
            File.Exists(Path.Combine(directory, ".last-media-tools-refresh")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Keeps_image_managed_ffmpeg_links()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "media-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.CreateSymbolicLink(Path.Combine(directory, "ffmpeg"), "/usr/bin/ffmpeg");
            File.CreateSymbolicLink(Path.Combine(directory, "ffprobe"), "/usr/bin/ffprobe");
            var downloader = Substitute.For<IYtDlpBinaryDownloader>();
            downloader.DownloadAllAsync(Arg.Any<BinaryDownloadOptions>(), Arg.Any<IProgress<BinaryDownloadProgress>?>(), Arg.Any<CancellationToken>())
                .Returns(new BinaryDownloadResult());
            var manager = new MediaToolsUpdateManager(downloader, new ConfigurationBuilder().Build(),
                NullLogger<MediaToolsUpdateManager>.Instance, directory);

            await manager.RefreshIfDueAsync(default);

            await downloader.Received(1).DownloadAllAsync(
                Arg.Is<BinaryDownloadOptions>(options => !options.DownloadFfmpeg && !options.DownloadFfprobe && options.DownloadYtDlp && options.DownloadDeno),
                Arg.Any<IProgress<BinaryDownloadProgress>?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
