using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Progress;

namespace YtDlpSharpLib.IntegrationTests;

[Category("Integration")]
[NotInParallel]
public sealed class OptionIntegrationTests
{
    [Test]
    public async Task DownloadAsync_UsesVimeoOutputTemplateRestrictFilenamesAndRecodeFormat()
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
                        Output = "%(extractor)s_%(id)s.%(ext)s",
                        RestrictFilenames = true
                    },
                    Download = IntegrationTestOptions.ShortSection,
                    VideoFormat = new YtDlpVideoFormatOptions
                    {
                        Format = "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best"
                    },
                    PostProcessing = new YtDlpPostProcessingOptions
                    {
                        RecodeVideo = VideoRecodeFormat.Mp4
                    }
                })
            });

        var file = await workspace.SingleMediaFileAsync();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(Path.GetExtension(file)).IsEqualTo(".mp4");
        await Assert.That(Path.GetFileNameWithoutExtension(file)).IsEqualTo("vimeo_1084537");
    }

    [Test]
    public async Task DownloadAsync_UsesBluegramsYouTubeOutputTemplateAndRecodeFormat()
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
                        Output = "%(extractor)s_%(title)s_%(upload_date)s.%(ext)s",
                        RestrictFilenames = true
                    },
                    Download = IntegrationTestOptions.ShortSection,
                    VideoFormat = new YtDlpVideoFormatOptions
                    {
                        Format = "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best"
                    },
                    PostProcessing = new YtDlpPostProcessingOptions
                    {
                        RecodeVideo = VideoRecodeFormat.Mp4
                    }
                })
            });

        var file = await workspace.SingleMediaFileAsync();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(Path.GetExtension(file)).IsEqualTo(".mp4");
        await Assert.That(Path.GetFileNameWithoutExtension(file)).IsEqualTo("youtube_TEST_VIDEO_20070221");
    }

    [Test]
    public async Task DownloadWithProgressAsync_EmitsProgressWhenNewlineOptionIsEnabled()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        var progress = new List<YtDlpProgress>();
        await foreach (var item in client.DownloadWithProgressAsync(
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
                                   VerbositySimulation = new YtDlpVerbositySimulationOptions
                                   {
                                       Newline = true
                                   },
                                   VideoFormat = new YtDlpVideoFormatOptions
                                   {
                                       Format = "best[ext=mp4]/best",
                                       MergeOutputFormat = DownloadMergeFormat.Mp4
                                   }
                               })
                           }))
        {
            progress.Add(item);
        }

        await Assert.That(progress).Contains(item => item.Phase == ProgressPhase.Downloading);
        await Assert.That(workspace.MediaFiles()).Count().IsEqualTo(1);
    }

    [Test]
    public async Task DownloadAsync_DoesNotWriteMediaWhenDateFilterRejectsVideo()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadAsync(
            IntegrationTestUrls.BluegramsYouTube,
            workspace.Path,
            new DownloadOptions
            {
                YtDlp = new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(id)s.%(ext)s"
                    },
                    VideoSelection = new YtDlpVideoSelectionOptions
                    {
                        Dateafter = "29991231"
                    },
                    VideoFormat = new YtDlpVideoFormatOptions
                    {
                        Format = "best[ext=mp4]/best"
                    }
                }
            });

        await Assert.That(workspace.MediaFiles()).IsEmpty();
    }
}
