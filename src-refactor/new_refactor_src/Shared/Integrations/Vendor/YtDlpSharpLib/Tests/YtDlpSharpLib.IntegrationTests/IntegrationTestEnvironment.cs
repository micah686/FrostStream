using YtDlpSharpLib.Options;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Provisioning;
using YtDlpSharpLib.Rendering;

namespace YtDlpSharpLib.IntegrationTests;

internal sealed class IntegrationTestEnvironment
{
    private const string YtDlpPathEnvVar = "YTDLP_INTEGRATION_YTDLP_PATH";
    private const string FfmpegPathEnvVar = "YTDLP_INTEGRATION_FFMPEG_PATH";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IntegrationTestEnvironment? _current;

    private IntegrationTestEnvironment(string ytDlpPath, string? ffmpegLocation)
    {
        YtDlpPath = ytDlpPath;
        FfmpegLocation = ffmpegLocation;
    }

    public string YtDlpPath { get; }

    public string? FfmpegLocation { get; }

    public static async Task<IntegrationTestEnvironment> GetAsync()
    {
        if (_current is not null)
        {
            return _current;
        }

        await Gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_current is not null)
            {
                return _current;
            }

            _current = await ResolveAsync(CancellationToken.None);
            return _current;
        }
        finally
        {
            Gate.Release();
        }
    }

    public YtDlpClient CreateClient() =>
        new(
            new YtDlpClientOptions { YtDlpExecutablePath = YtDlpPath },
            new YtDlpProcessFactory(),
            new YtDlpArgumentRenderer(),
            TimeProvider.System);

    public YtDlpOptions WithFfmpeg(YtDlpOptions options)
    {
        if (string.IsNullOrWhiteSpace(FfmpegLocation))
        {
            return options;
        }

        return options with
        {
            PostProcessing = options.PostProcessing with
            {
                FfmpegLocation = FfmpegLocation
            }
        };
    }

    private static async Task<IntegrationTestEnvironment> ResolveAsync(CancellationToken ct)
    {
        var configuredYtDlp = Environment.GetEnvironmentVariable(YtDlpPathEnvVar);
        var configuredFfmpeg = Environment.GetEnvironmentVariable(FfmpegPathEnvVar);
        var ffmpegPath = !string.IsNullOrWhiteSpace(configuredFfmpeg)
            ? configuredFfmpeg
            : FindExecutableOnPath("ffmpeg");
        if (!string.IsNullOrWhiteSpace(configuredYtDlp))
        {
            return new IntegrationTestEnvironment(
                configuredYtDlp,
                ResolveFfmpegLocation(ffmpegPath));
        }

        var pathYtDlp = FindExecutableOnPath("yt-dlp");
        if (!string.IsNullOrWhiteSpace(pathYtDlp))
        {
            return new IntegrationTestEnvironment(
                pathYtDlp,
                ResolveFfmpegLocation(ffmpegPath));
        }

        var binaryDirectory = Path.Combine(Path.GetTempPath(), "YtDlpSharpLib.IntegrationTests", "binaries");
        try
        {
            using var downloader = new YtDlpBinaryDownloader(new YtDlpBinaryDownloaderOptions());
            var result = await downloader.DownloadAllAsync(
                new BinaryDownloadOptions
                {
                    Directory = binaryDirectory,
                    DownloadDeno = false,
                    DownloadFfmpeg = ffmpegPath is null,
                    DownloadFfprobe = false,
                    SkipExisting = true
                },
                ct: ct);

            if (string.IsNullOrWhiteSpace(result.YtDlpPath))
            {
                Skip.Test("yt-dlp could not be resolved or downloaded for integration tests.");
            }

            return new IntegrationTestEnvironment(
                result.YtDlpPath!,
                ResolveFfmpegLocation(ffmpegPath ?? result.FfmpegPath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Skip.Test(
                $"yt-dlp integration tests require network access or {YtDlpPathEnvVar}/{FfmpegPathEnvVar}. " +
                $"Binary setup failed: {ex.Message}");
            throw;
        }
    }

    private static string? ResolveFfmpegLocation(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return null;
        }

        return Path.GetDirectoryName(Path.GetFullPath(ffmpegPath));
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, fileName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
