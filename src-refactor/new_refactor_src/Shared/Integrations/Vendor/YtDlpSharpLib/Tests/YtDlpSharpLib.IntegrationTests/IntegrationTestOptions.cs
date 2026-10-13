using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.IntegrationTests;

internal static class IntegrationTestOptions
{
    public static YtDlpDownloadOptions ShortSection => new()
    {
        DownloadSections = ["*0-5"]
    };
}
