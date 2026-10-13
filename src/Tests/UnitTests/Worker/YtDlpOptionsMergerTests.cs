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

        // A caller-supplied ffmpeg path is sanitized away; only the Worker may set it.
        merged.PostProcessing.FfmpegLocation.ShouldBeNull();
        merged.Filesystem.Cookies.ShouldBe("/user/cookies.txt");
        merged.Filesystem.NoCookies.ShouldBeTrue();
        merged.Filesystem.CookiesFromBrowser.ShouldBe("firefox");
    }

    [Test]
    public void Merge_Strips_Pipeline_Breaking_Options()
    {
        var userOptions = new YtDlpOptions
        {
            AdvancedArguments = [RawYtDlpArgument.Create("--exec", "rm -rf /")],
            VerbositySimulation = new YtDlpVerbositySimulationOptions
            {
                Simulate = true,
                SkipDownload = true,
                DumpSingleJson = true,
                Print = ["filename"]
            },
            PostProcessing = new YtDlpPostProcessingOptions
            {
                Exec = ["touch /tmp/pwned"],
                FfmpegLocation = "/user/ffmpeg",
                EmbedMetadata = true
            },
            Filesystem = new YtDlpFilesystemOptions
            {
                Paths = "home:/elsewhere",
                Output = "custom.%(ext)s",
                BatchFile = "/etc/passwd",
                LoadInfoJson = "/etc/passwd",
                CacheDir = "/root"
            },
            VideoSelection = new YtDlpVideoSelectionOptions { DownloadArchive = "/data/archive.txt" },
            General = new YtDlpGeneralOptions
            {
                Update = true,
                PluginDirs = ["/plugins"],
                ConfigLocations = ["/etc/yt-dlp.conf"]
            },
            VideoFormat = new YtDlpVideoFormatOptions { ListFormats = true, Format = "best" }
        };

        var merged = YtDlpOptionsMerger.Merge(userOptions, ffmpegLocation: null, cookieFilePath: null);

        merged.AdvancedArguments.ShouldBeEmpty();
        merged.VerbositySimulation.ShouldBe(new YtDlpVerbositySimulationOptions());
        merged.PostProcessing.Exec.ShouldBeEmpty();
        merged.PostProcessing.FfmpegLocation.ShouldBeNull();
        merged.Filesystem.Paths.ShouldBeNull();
        merged.Filesystem.Output.ShouldBeNull();
        merged.Filesystem.BatchFile.ShouldBeNull();
        merged.Filesystem.LoadInfoJson.ShouldBeNull();
        merged.Filesystem.CacheDir.ShouldBeNull();
        merged.VideoSelection.DownloadArchive.ShouldBeNull();
        merged.General.Update.ShouldBeFalse();
        merged.General.PluginDirs.ShouldBeEmpty();
        merged.General.ConfigLocations.ShouldBeEmpty();
        merged.VideoFormat.ListFormats.ShouldBeFalse();

        // Benign options in the same groups survive.
        merged.PostProcessing.EmbedMetadata.ShouldBeTrue();
        merged.VideoFormat.Format.ShouldBe("best");
    }

    [Test]
    public void Merge_Preserves_Authentication_Workarounds_And_Cookie_Options()
    {
        var userOptions = new YtDlpOptions
        {
            Authentication = new YtDlpAuthenticationOptions
            {
                Username = "user",
                Password = "secret",
                VideoPassword = "vidpass"
            },
            Workarounds = new YtDlpWorkaroundsOptions
            {
                NoCheckCertificates = true,
                SleepRequests = 1.5,
                AddHeaders = ["Referer: https://example.com"]
            },
            Network = new YtDlpNetworkOptions { Proxy = "socks5://127.0.0.1:1080" },
            SponsorBlock = new YtDlpSponsorBlockOptions { SponsorblockApi = "https://sb.example.com" },
            Filesystem = new YtDlpFilesystemOptions
            {
                Cookies = "/user/cookies.txt",
                CookiesFromBrowser = "firefox"
            }
        };

        var merged = YtDlpOptionsMerger.Merge(userOptions, ffmpegLocation: null, cookieFilePath: null);

        merged.Authentication.ShouldBe(userOptions.Authentication);
        merged.Workarounds.NoCheckCertificates.ShouldBeTrue();
        merged.Workarounds.SleepRequests.ShouldBe(1.5);
        merged.Workarounds.AddHeaders.ShouldBe(["Referer: https://example.com"]);
        merged.Network.Proxy.ShouldBe("socks5://127.0.0.1:1080");
        merged.SponsorBlock.SponsorblockApi.ShouldBe("https://sb.example.com");
        merged.Filesystem.Cookies.ShouldBe("/user/cookies.txt");
        merged.Filesystem.CookiesFromBrowser.ShouldBe("firefox");
    }
}
