using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.IntegrationTests;

[Category("Integration")]
[NotInParallel]
public sealed class DownloadIntegrationTests
{
    [Test]
    public async Task DownloadAsync_DownloadsVimeoVideoFile()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadAsync(
            IntegrationTestUrls.Vimeo,
            workspace.Path,
            new DownloadOptions
            {
                YtDlp = environment.WithFfmpeg(new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(id)s.%(ext)s"
                    },
                    Download = IntegrationTestOptions.ShortSection,
                    VideoFormat = new YtDlpVideoFormatOptions
                    {
                        Format = "best[ext=mp4]/best",
                        MergeOutputFormat = DownloadMergeFormat.Mp4
                    }
                })
            });

        var file = await workspace.SingleMediaFileAsync();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(Path.GetExtension(file)).IsEqualTo(".mp4");
        await Assert.That(Path.GetFileNameWithoutExtension(file)).IsEqualTo("1084537");
    }

    [Test]
    public async Task DownloadAudioAsync_ExtractsVimeoMp3()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadAudioAsync(
            IntegrationTestUrls.Vimeo,
            workspace.Path,
            new AudioDownloadOptions
            {
                AudioFormat = AudioConversionFormat.Mp3,
                YtDlp = environment.WithFfmpeg(new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(id)s.%(ext)s"
                    },
                    Download = IntegrationTestOptions.ShortSection
                })
            });

        var file = await workspace.SingleMediaFileAsync();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(Path.GetExtension(file)).IsEqualTo(".mp3");
        await Assert.That(Path.GetFileNameWithoutExtension(file)).IsEqualTo("1084537");
    }

    [Test]
    public async Task DownloadAsync_DownloadsBluegramsYouTubeVideoToMkv()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadAsync(
            IntegrationTestUrls.BluegramsYouTube,
            workspace.Path,
            new DownloadOptions
            {
                YtDlp = environment.WithFfmpeg(new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(title)s.%(ext)s"
                    },
                    Download = IntegrationTestOptions.ShortSection,
                    VideoFormat = new YtDlpVideoFormatOptions
                    {
                        Format = "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
                        MergeOutputFormat = DownloadMergeFormat.Mkv
                    }
                })
            });

        var file = await workspace.SingleMediaFileAsync();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(Path.GetExtension(file)).IsEqualTo(".mkv");
        await Assert.That(Path.GetFileNameWithoutExtension(file)).IsEqualTo("TEST VIDEO");
    }

    [Test]
    public async Task DownloadAudioAsync_ExtractsBluegramsYouTubeMp3()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadAudioAsync(
            IntegrationTestUrls.BluegramsYouTube,
            workspace.Path,
            new AudioDownloadOptions
            {
                AudioFormat = AudioConversionFormat.Mp3,
                YtDlp = environment.WithFfmpeg(new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(title)s.%(ext)s"
                    },
                    Download = IntegrationTestOptions.ShortSection
                })
            });

        var file = await workspace.SingleMediaFileAsync();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(Path.GetExtension(file)).IsEqualTo(".mp3");
        await Assert.That(Path.GetFileNameWithoutExtension(file)).IsEqualTo("TEST VIDEO");
    }
}
