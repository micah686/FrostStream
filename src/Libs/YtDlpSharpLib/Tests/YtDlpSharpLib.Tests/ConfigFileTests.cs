using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.Tests;

public sealed class ConfigFileTests
{
    [Fact]
    public void FromString_ParsesKnownFlagsAliasesCollectionsAndAdvancedArguments()
    {
        var options = YtDlpOptions.FromString(
            [
                "# comment",
                "--socket-timeout 1.25",
                "-o \"%(title)s.%(ext)s\"",
                "--merge-output-format mp4",
                "--config-locations user.conf",
                "--config-locations override.conf",
                "--alias get-audio \"-x --audio-format mp3\"",
                "--custom-flag=value"
            ]);

        Assert.Equal(1.25, options.Network.SocketTimeout);
        Assert.Equal("%(title)s.%(ext)s", options.Filesystem.Output);
        Assert.Equal(DownloadMergeFormat.Mp4, options.VideoFormat.MergeOutputFormat);
        Assert.Equal(["user.conf", "override.conf"], options.General.ConfigLocations);
        var alias = Assert.Single(options.General.Alias);
        Assert.Equal(["get-audio", "-x --audio-format mp3"], alias);
        var advanced = Assert.Single(options.AdvancedArguments);
        Assert.Equal("--custom-flag", advanced.Name);
        Assert.Equal("value", advanced.Value);
    }

    [Fact]
    public void GetOptionFlagsAndToString_RenderDeterministicArguments()
    {
        var options = new YtDlpOptions
        {
            Filesystem = new YtDlpFilesystemOptions
            {
                Output = "%(title)s.%(ext)s"
            },
            PostProcessing = new YtDlpPostProcessingOptions
            {
                ExtractAudio = true,
                AudioFormat = AudioConversionFormat.Mp3
            }
        };

        Assert.Equal(
            ["--output", "%(title)s.%(ext)s", "--extract-audio", "--audio-format", "mp3"],
            options.GetOptionFlags());
        Assert.Equal("--output '%(title)s.%(ext)s' --extract-audio --audio-format mp3", options.ToString());
    }

    [Fact]
    public void WriteConfigFile_RoundTripsThroughLoadConfigFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "YtDlpSharpLib.Tests", Guid.NewGuid().ToString("N"), "yt-dlp.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var original = new YtDlpOptions
            {
                General = new YtDlpGeneralOptions
                {
                    ConfigLocations = ["first.conf", "second.conf"]
                },
                VerbositySimulation = new YtDlpVerbositySimulationOptions
                {
                    SkipDownload = true
                },
                PostProcessing = new YtDlpPostProcessingOptions
                {
                    Exec = ["after_move:echo %(filepath)q"]
                },
                AdvancedArguments =
                [
                    new RawYtDlpArgument
                    {
                        Name = "--custom-flag",
                        Value = "custom value"
                    }
                ]
            };

            original.WriteConfigFile(path);
            var loaded = YtDlpOptions.LoadConfigFile(path);

            Assert.Equal(original.GetOptionFlags(), loaded.GetOptionFlags());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
