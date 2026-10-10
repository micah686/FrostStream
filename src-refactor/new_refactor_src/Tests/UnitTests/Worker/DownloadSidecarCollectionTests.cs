using Shouldly;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Worker;

public sealed class DownloadSidecarCollectionTests
{
    [Test]
    public async Task FindDownloadedMediaFile_Ignores_Thumbnail_And_Subtitle_Sidecars()
    {
        using var dir = new TempDir();
        // The thumbnail is written last so it has the most recent mtime — the detector must still
        // pick the media file, not the thumbnail.
        await File.WriteAllTextAsync(dir.At("media.mp4"), "video");
        await File.WriteAllTextAsync(dir.At("media.info.json"), "{}");
        await File.WriteAllTextAsync(dir.At("media.en.vtt"), "WEBVTT");
        await File.WriteAllBytesAsync(dir.At("media.webp"), "thumb"u8.ToArray());

        var found = DownloadCommandsConsumerService.FindDownloadedMediaFile(dir.Root);

        Path.GetFileName(found).ShouldBe("media.mp4");
    }

    [Test]
    public async Task ResolveAssetSidecarsAsync_Collects_Thumbnail_And_Caption_Languages()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.At("media.mkv"), "video");
        await File.WriteAllTextAsync(dir.At("media.info.json"), "{}");
        await File.WriteAllBytesAsync(dir.At("media.webp"), "thumb"u8.ToArray());
        await File.WriteAllTextAsync(dir.At("media.en.vtt"), "WEBVTT-en");
        await File.WriteAllTextAsync(dir.At("media.es-ES.srt"), "1\n00:00 --> 00:01\nhola");

        var (thumbnail, captions) = await DownloadCommandsConsumerService.ResolveAssetSidecarsAsync(
            dir.Root,
            dir.At("media.mkv"));

        thumbnail.ShouldNotBeNull();
        thumbnail!.FileName.ShouldBe("media.webp");
        thumbnail.ContentHashXxh128.ShouldNotBeNullOrWhiteSpace();

        captions.Select(c => c.LanguageCode).OrderBy(x => x).ShouldBe(["en", "es-ES"]);
        captions.ShouldAllBe(c => !string.IsNullOrWhiteSpace(c.ContentHashXxh128));
    }

    [Test]
    [Arguments("media.en.vtt", "en")]
    [Arguments("media.en-US.srt", "en-US")]
    [Arguments("media.pt-BR.ass", "pt-BR")]
    [Arguments("media.vtt", "und")]
    public async Task ParseCaptionLanguage_Extracts_Language_Tag(string fileName, string expected)
    {
        DownloadCommandsConsumerService.ParseCaptionLanguage(fileName).ShouldBe(expected);
        await Task.CompletedTask;
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"froststream-sidecar-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Root);

        public string At(string fileName) => System.IO.Path.Combine(Root, fileName);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
