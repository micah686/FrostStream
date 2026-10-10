using System.Globalization;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Rendering;

namespace YtDlpSharpLib.Tests;

public sealed class RendererTests
{
    [Fact]
    public void Render_HandlesGeneratedOptionsAndAdvancedArguments()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var renderer = new YtDlpArgumentRenderer();

            var options = new YtDlpOptions
            {
                General = new YtDlpGeneralOptions
                {
                    ConfigLocations = ["user.conf", "override.conf"]
                },
                Network = new YtDlpNetworkOptions
                {
                    SocketTimeout = 1.25
                },
                VideoFormat = new YtDlpVideoFormatOptions
                {
                    Format = "best",
                    MergeOutputFormat = DownloadMergeFormat.Mp4
                },
                Subtitle = new YtDlpSubtitleOptions
                {
                    SubFormat = SubtitleFormat.Srt
                },
                PostProcessing = new YtDlpPostProcessingOptions
                {
                    ExtractAudio = true,
                    AudioFormat = AudioConversionFormat.Mp3,
                    RemuxVideo = VideoContainer.Mkv,
                    RecodeVideo = VideoRecodeFormat.Mp4,
                    ConvertSubs = SubtitleFormat.Vtt,
                    Fixup = YtDlpPostProcessingFixup.DetectOrWarn
                },
                AdvancedArguments =
                [
                    new RawYtDlpArgument
                    {
                        Name = "--custom",
                        Value = "first",
                        Values = ["second"]
                    }
                ]
            };

            Assert.Equal(
                [
                    "--config-locations",
                    "user.conf",
                    "--config-locations",
                    "override.conf",
                    "--socket-timeout",
                    "1.25",
                    "--format",
                    "best",
                    "--merge-output-format",
                    "mp4",
                    "--sub-format",
                    "srt",
                    "--extract-audio",
                    "--audio-format",
                    "mp3",
                    "--remux-video",
                    "mkv",
                    "--recode-video",
                    "mp4",
                    "--fixup",
                    "detect_or_warn",
                    "--convert-subs",
                    "vtt",
                    "--custom",
                    "first",
                    "second"
                ],
                renderer.Render(options));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Render_HandlesGeneratedMultiValueOptions()
    {
        var renderer = new YtDlpArgumentRenderer();
        var options = new YtDlpOptions
        {
            General = new YtDlpGeneralOptions
            {
                Alias =
                [
                    ["get-audio", "-x --audio-format mp3"]
                ]
            }
        };

        Assert.Equal(["--alias", "get-audio", "-x --audio-format mp3"], renderer.Render(options));
    }
}
