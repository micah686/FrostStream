using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaProcessor.Ffmpeg;

/// <summary>Media probe result reduced to what the encode planners need.</summary>
public sealed record MediaProbe
{
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int? VideoHeight { get; init; }
    public int? DurationSeconds { get; init; }
    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;
}

/// <summary>Runs ffmpeg/ffprobe as child processes for the rendition services.</summary>
public sealed class FfmpegRunner(IOptions<MediaProcessorOptions> options, ILogger<FfmpegRunner> logger)
{
    public async Task RunFfmpegAsync(string args, string? workingDirectory, CancellationToken cancellationToken)
    {
        logger.LogDebug("ffmpeg {Args}", args);
        var (exitCode, _, stderr) = await RunProcessAsync(options.Value.FfmpegPath, args, workingDirectory, cancellationToken);
        if (exitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {exitCode}: {LastLines(stderr, 8)}");
    }

    public async Task<MediaProbe> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        var args = $"-hide_banner -v error -print_format json -show_format -show_streams {Quote(inputPath)}";
        var (exitCode, stdout, stderr) = await RunProcessAsync(options.Value.FfprobePath, args, workingDirectory: null, cancellationToken);
        if (exitCode != 0)
            throw new InvalidOperationException($"ffprobe exited with code {exitCode}: {LastLines(stderr, 8)}");

        using var document = JsonDocument.Parse(stdout);
        string? videoCodec = null;
        string? audioCodec = null;
        int? videoHeight = null;

        if (document.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var type) ? type.GetString() : null;
                var codecName = stream.TryGetProperty("codec_name", out var name) ? name.GetString() : null;
                switch (codecType)
                {
                    case "video" when videoCodec is null && !IsAttachedPicture(stream):
                        videoCodec = codecName;
                        if (stream.TryGetProperty("height", out var height) && height.TryGetInt32(out var parsedHeight))
                            videoHeight = parsedHeight;
                        break;
                    case "audio" when audioCodec is null:
                        audioCodec = codecName;
                        break;
                }
            }
        }

        int? durationSeconds = null;
        if (document.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var duration) &&
            double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
        {
            durationSeconds = (int)Math.Round(parsedDuration);
        }

        return new MediaProbe
        {
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            VideoHeight = videoHeight,
            DurationSeconds = durationSeconds
        };
    }

    /// <summary>Cover art embedded in audio files shows up as a video stream; it is not a video track.</summary>
    private static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var disposition) &&
           disposition.TryGetProperty("attached_pic", out var attachedPic) &&
           attachedPic.TryGetInt32(out var value) && value == 1;

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        string args,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            },
            EnableRaisingEvents = true
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    public static async Task<string> ComputeXxHash128Async(string path, CancellationToken cancellationToken)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await using var stream = File.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }

            Span<byte> hash = stackalloc byte[16];
            hasher.GetCurrentHash(hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static string LastLines(string value, int count)
        => string.Join('\n', value.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count));

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort; stale scratch is safe to remove on next maintenance pass.
        }
    }
}
