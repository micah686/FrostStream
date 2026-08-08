using YtDlpSharpLib.Progress;

namespace YtDlpSharpLib.Tests;

/// <summary>
/// yt-dlp right-justifies the size/speed/eta fields in its default progress line to a fixed
/// character width (FileDownloader.format_speed/format_eta/_format_bytes in yt-dlp's own
/// source), so real output has variable leading padding — not a single separator space. These
/// lines were generated with yt-dlp's actual formatting functions (via a local Python
/// interpreter against the installed yt-dlp package) rather than hand-typed, to catch parsing
/// bugs that only show up against authentically-padded input.
/// </summary>
public sealed class ProgressLineParserTests
{
    [Fact]
    public void TryParse_PaddedTotalBytesLine_ParsesTotalBytesSpeedAndEta()
    {
        var ok = ProgressLineParser.TryParse(
            "[download]  45.2% of   10.00MiB at    1.20MiB/s ETA    00:08", out var progress);

        Assert.True(ok);
        Assert.Equal(ProgressPhase.Downloading, progress.Phase);
        Assert.Equal(45.2, progress.Percent);
        Assert.Equal(10L * 1024 * 1024, progress.TotalBytes);
        Assert.Equal((long)(10L * 1024 * 1024 * 0.452), progress.DownloadedBytes);
        Assert.Equal("1.20MiB/s", progress.Speed);
        Assert.Equal(TimeSpan.FromSeconds(8), progress.Eta);
    }

    [Fact]
    public void TryParse_PaddedEstimatedSizeWithUnknownSpeed_ParsesTotalBytesAndEtaButNullsSpeed()
    {
        var ok = ProgressLineParser.TryParse(
            "[download]   7.9% of ~   3.00MiB at  Unknown B/s ETA    00:45", out var progress);

        Assert.True(ok);
        Assert.Equal(7.9, progress.Percent);
        Assert.Equal(3L * 1024 * 1024, progress.TotalBytes);
        Assert.Null(progress.Speed);
        Assert.Equal(TimeSpan.FromSeconds(45), progress.Eta);
    }

    [Fact]
    public void TryParse_PaddedLineWithFragmentSuffix_IgnoresTrailingFragmentInfo()
    {
        var ok = ProgressLineParser.TryParse(
            "[download]  99.9% of   50.00MiB at   15.00MiB/s ETA    00:00 (frag 12/12)", out var progress);

        Assert.True(ok);
        Assert.Equal(99.9, progress.Percent);
        Assert.Equal(50L * 1024 * 1024, progress.TotalBytes);
        Assert.Equal("15.00MiB/s", progress.Speed);
        Assert.Equal(TimeSpan.Zero, progress.Eta);
    }

    [Fact]
    public void TryParse_UnpaddedLine_StillParsesEveryField()
    {
        // The non-padded shape used elsewhere in the suite (e.g. YtDlpClientTests); must keep
        // working since not every value needs padding to reach its field width.
        var ok = ProgressLineParser.TryParse(
            "[download]  50.0% of 10.00MiB at 2.00MiB/s ETA 00:05", out var progress);

        Assert.True(ok);
        Assert.Equal(50.0, progress.Percent);
        Assert.Equal(10L * 1024 * 1024, progress.TotalBytes);
        Assert.Equal(5L * 1024 * 1024, progress.DownloadedBytes);
        Assert.Equal("2.00MiB/s", progress.Speed);
        Assert.Equal(TimeSpan.FromSeconds(5), progress.Eta);
    }
}
