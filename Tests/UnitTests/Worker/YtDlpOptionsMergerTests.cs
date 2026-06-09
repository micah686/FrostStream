using Shouldly;
using TUnit.Core;
using Worker.Services;
using YtDlpSharpLib.Options;

namespace UnitTests.Worker;

public sealed class YtDlpOptionsMergerTests
{
    [Test]
    public void Merge_Preserves_User_Options_And_Applies_Worker_Overrides()
    {
        var userOptions = new YtDlpOptions
        {
            General = new YtDlpGeneralOptions { IgnoreErrors = true },
            Filesystem = new YtDlpFilesystemOptions
            {
                Cookies = "/user/cookies.txt",
                NoCookies = true,
                CookiesFromBrowser = "firefox"
            }
        };

        var merged = YtDlpOptionsMerger.Merge(userOptions, "/worker/ffmpeg", "/worker/cookies.txt");

        merged.General.IgnoreErrors.ShouldBeTrue();
        merged.PostProcessing.FfmpegLocation.ShouldBe("/worker/ffmpeg");
        merged.Filesystem.Cookies.ShouldBe("/worker/cookies.txt");
        merged.Filesystem.NoCookies.ShouldBeFalse();
        merged.Filesystem.CookiesFromBrowser.ShouldBeNull();
    }

    [Test]
    public void Merge_Ignores_Blank_Worker_Overrides()
    {
        var userOptions = new YtDlpOptions
        {
            PostProcessing = new YtDlpPostProcessingOptions { FfmpegLocation = "/user/ffmpeg" },
            Filesystem = new YtDlpFilesystemOptions
            {
                Cookies = "/user/cookies.txt",
                NoCookies = true,
                CookiesFromBrowser = "firefox"
            }
        };

        var merged = YtDlpOptionsMerger.Merge(userOptions, " ", null);

        merged.PostProcessing.FfmpegLocation.ShouldBe("/user/ffmpeg");
        merged.Filesystem.Cookies.ShouldBe("/user/cookies.txt");
        merged.Filesystem.NoCookies.ShouldBeTrue();
        merged.Filesystem.CookiesFromBrowser.ShouldBe("firefox");
    }
}
