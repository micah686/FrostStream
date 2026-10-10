using System.Text.Json;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.IntegrationTests;

[Category("Integration")]
[NotInParallel]
public sealed class MetadataIntegrationTests
{
    [Test]
    public async Task GetVideoInfoAsync_ReturnsMetadataForVimeoVideo()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        var client = environment.CreateClient();

        var info = await client.GetVideoInfoAsync(IntegrationTestUrls.Vimeo);

        await Assert.That(info.Id).IsEqualTo("1084537");
        await Assert.That(info.Title).IsNotEmpty();
        await Assert.That(info.Extractor).Contains("vimeo", StringComparison.OrdinalIgnoreCase);
        await Assert.That(info.WebpageUrl).IsEqualTo(IntegrationTestUrls.Vimeo);
        await Assert.That(info.Formats).IsNotEmpty();
        await Assert.That(info.Thumbnails).IsNotNull();
    }

    [Test]
    public async Task GetVideoInfoAsync_ReturnsMetadataForBluegramsYouTubeVideo()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        var client = environment.CreateClient();

        var info = await client.GetVideoInfoAsync(IntegrationTestUrls.BluegramsYouTube);

        await Assert.That(info.Id).IsEqualTo("C0DPdy98e4c");
        await Assert.That(info.Title).IsEqualTo("TEST VIDEO");
        await Assert.That(info.Extractor).Contains("youtube", StringComparison.OrdinalIgnoreCase);
        await Assert.That(info.UploadDate).IsEqualTo("20070221");
        await Assert.That(info.ParsedUploadDate).IsEqualTo(new DateOnly(2007, 2, 21));
        await Assert.That(info.Formats).IsNotEmpty();
    }

    [Test]
    public async Task DownloadMetadataAsync_WritesInfoJsonWithoutMediaFile()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadMetadataAsync(
            IntegrationTestUrls.Vimeo,
            workspace.Path,
            new MetadataDownloadOptions
            {
                YtDlp = new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(id)s.%(ext)s"
                    }
                }
            });

        var infoJson = await workspace.SingleFileAsync("*.info.json");
        await Assert.That(File.Exists(infoJson)).IsTrue();
        await Assert.That(workspace.MediaFiles()).IsEmpty();

        await using var stream = File.OpenRead(infoJson);
        using var document = await JsonDocument.ParseAsync(stream);
        await Assert.That(document.RootElement.GetProperty("id").GetString()).IsEqualTo("1084537");
    }

    [Test]
    public async Task DownloadMetadataAsync_WritesBluegramsYouTubeInfoJsonWithOutputTemplate()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadMetadataAsync(
            IntegrationTestUrls.BluegramsYouTube,
            workspace.Path,
            new MetadataDownloadOptions
            {
                YtDlp = new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(extractor)s_%(title)s_%(upload_date)s.%(ext)s",
                        RestrictFilenames = true
                    }
                }
            });

        var infoJson = await workspace.SingleFileAsync("*.info.json");
        await Assert.That(Path.GetFileName(infoJson)).IsEqualTo("youtube_TEST_VIDEO_20070221.info.json");

        await using var stream = File.OpenRead(infoJson);
        using var document = await JsonDocument.ParseAsync(stream);
        await Assert.That(document.RootElement.GetProperty("title").GetString()).IsEqualTo("TEST VIDEO");
    }

    [Test]
    public async Task DownloadMetadataAsync_WritesThumbnailWhenRequested()
    {
        var environment = await IntegrationTestEnvironment.GetAsync();
        await using var workspace = TemporaryWorkspace.Create();

        var client = environment.CreateClient();
        await client.DownloadMetadataAsync(
            IntegrationTestUrls.Vimeo,
            workspace.Path,
            new MetadataDownloadOptions
            {
                WriteThumbnail = true,
                YtDlp = new YtDlpOptions
                {
                    Filesystem = new YtDlpFilesystemOptions
                    {
                        Output = "%(id)s.%(ext)s"
                    }
                }
            });

        await Assert.That(Directory.GetFiles(workspace.Path, "*.info.json", SearchOption.TopDirectoryOnly)).Count().IsEqualTo(1);
        await Assert.That(workspace.ThumbnailFiles()).IsNotEmpty();
        await Assert.That(workspace.MediaFiles()).IsEmpty();
    }
}
