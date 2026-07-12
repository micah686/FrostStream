using Microsoft.Extensions.Logging;
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
        string? cookieFilePath,
        ILogger? logger = null)
    {
        var options = Sanitize(userOptions ?? new YtDlpOptions(), logger);

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

    /// <summary>
    /// Strips caller options the pipeline cannot survive: output/path redirection (the Worker
    /// locates results via its temp dir and fixed <c>media.%(ext)s</c> template), the
    /// simulate/print/dump and list-and-exit flags (metadata parsing consumes raw stdout and
    /// every job must produce a file), <c>--exec</c> (arbitrary command execution), server
    /// filesystem references, config/plugin loading, and the raw <c>AdvancedArguments</c>
    /// passthrough (rendered last, so it would override every typed protection).
    /// Authentication, workarounds, and cookie options are deliberately left untouched;
    /// cookies are governed by the job's cookie profile in <see cref="Merge"/>.
    /// </summary>
    private static YtDlpOptions Sanitize(YtDlpOptions options, ILogger? logger)
    {
        var sanitized = options with
        {
            AdvancedArguments = [],
            VerbositySimulation = new YtDlpVerbositySimulationOptions(),
            Deprecated = new YtDlpDeprecatedOptions(),
            PostProcessing = options.PostProcessing with
            {
                Exec = [],
                NoExec = false,
                FfmpegLocation = null,
                UsePostprocessor = []
            },
            Filesystem = options.Filesystem with
            {
                Paths = null,
                Output = null,
                BatchFile = null,
                LoadInfoJson = null,
                CacheDir = null
            },
            VideoSelection = options.VideoSelection with { DownloadArchive = null },
            General = options.General with
            {
                Help = false,
                Version = false,
                Update = false,
                UpdateTo = null,
                ListExtractors = false,
                ExtractorDescriptions = false,
                ConfigLocations = [],
                PluginDirs = [],
                JsRuntimes = [],
                RemoteComponents = [],
                Alias = [],
                PresetAlias = []
            },
            VideoFormat = options.VideoFormat with { ListFormats = false },
            Subtitle = options.Subtitle with { ListSubs = false },
            Thumbnail = options.Thumbnail with { ListThumbnails = false },
            Network = options.Network with { ListImpersonateTargets = false }
        };

        if (logger?.IsEnabled(LogLevel.Debug) is true)
        {
            // Compare rendered argv and report removed flag names only (never values).
            var removed = options.GetOptionFlags()
                .Except(sanitized.GetOptionFlags())
                .Where(static token => token.StartsWith("--", StringComparison.Ordinal))
                .ToList();
            if (removed.Count > 0)
            {
                logger.LogDebug(
                    "Stripped caller-supplied yt-dlp options the pipeline does not support: {Flags}",
                    string.Join(' ', removed));
            }
        }

        return sanitized;
    }
}
