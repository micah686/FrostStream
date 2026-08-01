using System.Globalization;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Rendering;

namespace YtDlpSharpLib.Tests;

public sealed class OptionsCompositionTests
{
    [Fact]
    public void OverrideOptions_MergesOnlySetOverrideValuesByDefault()
    {
        var baseOptions = new YtDlpOptions
        {
            General = new YtDlpGeneralOptions
            {
                IgnoreErrors = true,
                ConfigLocations = ["base.conf"]
            },
            Filesystem = new YtDlpFilesystemOptions
            {
                Paths = "home:/base",
                Output = "%(title)s.%(ext)s",
                RestrictFilenames = true
            },
            VideoFormat = new YtDlpVideoFormatOptions
            {
                Format = "best"
            },
            AdvancedArguments =
            [
                new RawYtDlpArgument { Name = "--base-only", Value = "1" }
            ]
        };

        var overrideOptions = new YtDlpOptions
        {
            General = new YtDlpGeneralOptions
            {
                ConfigLocations = ["override.conf"]
            },
            Filesystem = new YtDlpFilesystemOptions
            {
                Output = "%(id)s.%(ext)s",
                NoRestrictFilenames = true
            },
            AdvancedArguments =
            [
                new RawYtDlpArgument { Name = "--override-only", Value = "2" }
            ]
        };

        var result = baseOptions.OverrideOptions(overrideOptions);

        Assert.True(result.General.IgnoreErrors);
        Assert.Equal(["override.conf"], result.General.ConfigLocations);
        Assert.Equal("home:/base", result.Filesystem.Paths);
        Assert.Equal("%(id)s.%(ext)s", result.Filesystem.Output);
        Assert.True(result.Filesystem.RestrictFilenames);
        Assert.True(result.Filesystem.NoRestrictFilenames);
        Assert.Equal("best", result.VideoFormat.Format);
        Assert.Equal("--override-only", Assert.Single(result.AdvancedArguments).Name);
    }

    [Fact]
    public void OverrideOptions_ForceOverrideCopiesUnsetValues()
    {
        var baseOptions = new YtDlpOptions
        {
            General = new YtDlpGeneralOptions
            {
                IgnoreErrors = true,
                ConfigLocations = ["base.conf"]
            },
            Filesystem = new YtDlpFilesystemOptions
            {
                Paths = "home:/base",
                Output = "%(title)s.%(ext)s",
                RestrictFilenames = true
            }
        };

        var result = baseOptions.OverrideOptions(new YtDlpOptions(), forceOverride: true);

        Assert.False(result.General.IgnoreErrors);
        Assert.Empty(result.General.ConfigLocations);
        Assert.Null(result.Filesystem.Paths);
        Assert.Null(result.Filesystem.Output);
        Assert.False(result.Filesystem.RestrictFilenames);
    }

    [Fact]
    public void OptionComparer_ComparesSequenceContents()
    {
        var left = new YtDlpOptions
        {
            General = new YtDlpGeneralOptions
            {
                ConfigLocations = ["one.conf", "two.conf"]
            }
        };
        var right = new YtDlpOptions
        {
            General = new YtDlpGeneralOptions
            {
                ConfigLocations = ["one.conf", "two.conf"]
            }
        };

        Assert.False(left.Equals(right));
        Assert.True(OptionComparer.Instance.Equals(left, right));
        Assert.Equal(
            OptionComparer.Instance.GetHashCode(left),
            OptionComparer.Instance.GetHashCode(right));
    }

    [Fact]
    public void CustomOptionHelpers_AddSetAndDeleteTypedArguments()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var options = new YtDlpOptions()
                .AddCustomOption("--ratio", 1.25)
                .AddCustomOption("--merge", DownloadMergeFormat.Mp4)
                .SetCustomOption("--ratio", 2.5)
                .DeleteCustomOption("--merge");

            Assert.Equal(
                ["--ratio", "2.5"],
                new YtDlpArgumentRenderer().Render(options));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void RawYtDlpArgumentCreate_FormatsAdditionalTypedValues()
    {
        var argument = RawYtDlpArgument.Create("--range", 1, [2, 3]);

        Assert.Equal("--range", argument.Name);
        Assert.Equal("1", argument.Value);
        Assert.Equal(["2", "3"], argument.Values);
    }
}
