using YtDlpSharpLib.Options;

namespace Worker.Services;

/// <summary>
/// Merges caller-supplied <see cref="YtDlpOptions"/> with Worker-mandated overrides
/// (ffmpeg path, cookie file, etc.). Caller options form the base; Worker overrides
/// take precedence so we never lose control of operational invariants.
/// </summary>
internal static class YtDlpOptionsMerger
{
    public static YtDlpOptions Merge(
        YtDlpOptions? userOptions,
        string? ffmpegLocation,
        string? cookieFilePath)
    {
        var options = userOptions ?? new YtDlpOptions();

        if (!string.IsNullOrWhiteSpace(ffmpegLocation))
        {
            options = options with
            {
                PostProcessing = options.PostProcessing with
                {
                    FfmpegLocation = ffmpegLocation
                }
            };
        }

        if (!string.IsNullOrWhiteSpace(cookieFilePath))
        {
            options = options with
            {
                Filesystem = options.Filesystem with
                {
                    Cookies = cookieFilePath,
                    NoCookies = false,
                    CookiesFromBrowser = null
                }
            };
        }

        return options;
    }
}
