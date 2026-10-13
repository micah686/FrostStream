using YtDlpSharpLib.Options;
using YtDlpSharpLib.Rendering;

namespace YtDlpSharpLib.Tests;

public sealed class DeprecatedOptionsTests
{
    [Fact]
    public void DeprecatedOptions_RenderLegacyFlags()
    {
#pragma warning disable CS0618
        var options = new YtDlpOptions
        {
            Deprecated = new YtDlpDeprecatedOptions
            {
                MatchTitle = "cats",
                MetadataFromTitle = "%(artist)s - %(title)s",
                HlsPreferNative = true,
                CnVerificationProxy = "http://proxy.test",
                AutonumberSize = 4
            }
        };
#pragma warning restore CS0618

        Assert.Equal(
            [
                "--match-title",
                "cats",
                "--metadata-from-title",
                "%(artist)s - %(title)s",
                "--hls-prefer-native",
                "--cn-verification-proxy",
                "http://proxy.test",
                "--autonumber-size",
                "4"
            ],
            new YtDlpArgumentRenderer().Render(options));
    }

    [Fact]
    public void ConfigParser_LoadsDeprecatedLegacyFlags()
    {
        var options = YtDlpOptions.FromString(
            [
                "--reject-title dogs",
                "--hls-prefer-ffmpeg",
                "--prefer-ffmpeg",
                "--avconv-location /usr/bin/avconv",
                "--youtube-skip-dash-manifest",
                "--write-annotations",
                "--load-pages",
                "--no-call-home",
                "--include-ads"
            ]);

#pragma warning disable CS0618
        Assert.Equal("dogs", options.Deprecated.RejectTitle);
        Assert.True(options.Deprecated.HlsPreferFfmpeg);
        Assert.True(options.Deprecated.PreferFfmpeg);
        Assert.Equal("/usr/bin/avconv", options.Deprecated.AvconvLocation);
        Assert.True(options.Deprecated.YoutubeSkipDashManifest);
        Assert.True(options.Deprecated.WriteAnnotations);
        Assert.True(options.Deprecated.LoadPages);
        Assert.True(options.Deprecated.NoCallHome);
        Assert.True(options.Deprecated.IncludeAds);
#pragma warning restore CS0618
    }
}
