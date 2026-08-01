using System.Globalization;
using YtDlpSharpLib;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Progress;

namespace ConsoleDownloader;

internal static class Program
{
    private const string VideoUrl = "https://vimeo.com/1084537";

    public static async Task<int> Main(string[] args)
    {
        var outputDirectory = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(Environment.CurrentDirectory, "downloads");

        Directory.CreateDirectory(outputDirectory);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Cancellation requested. Waiting for yt-dlp to stop...");
        };

        var client = new YtDlpClient();
        var options = new DownloadOptions
        {
            YtDlp = new YtDlpOptions
            {
                VideoSelection = new YtDlpVideoSelectionOptions
                {
                    NoPlaylist = true
                },
                VerbositySimulation = new YtDlpVerbositySimulationOptions
                {
                    Newline = true,
                    Progress = true,
                    ProgressDelta = 1
                }
            }
        };

        Console.WriteLine($"Downloading {VideoUrl}");
        Console.WriteLine($"Output directory: {outputDirectory}");

        try
        {
            await foreach (var progress in client.DownloadWithProgressAsync(
                               VideoUrl,
                               outputDirectory,
                               options,
                               cts.Token))
            {
                Console.WriteLine(FormatProgress(progress));
            }

            Console.WriteLine("Download complete.");
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Download cancelled.");
            return 130;
        }
        catch (YtDlpNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Install yt-dlp and make sure it is available on PATH.");
            return 1;
        }
        catch (YtDlpProcessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (!string.IsNullOrWhiteSpace(ex.LastStderrLines))
            {
                Console.Error.WriteLine(ex.LastStderrLines);
            }

            return ex.ExitCode ?? 1;
        }
        catch (YtDlpException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode ?? 1;
        }
    }

    private static string FormatProgress(YtDlpProgress progress)
    {
        return progress.Phase switch
        {
            ProgressPhase.Downloading when progress.Percent is { } percent => FormatDownloadProgress(progress, percent),
            ProgressPhase.Finished => FormatMessage("finished", progress.Destination ?? progress.Message),
            ProgressPhase.Completed => FormatMessage("complete", progress.Message),
            ProgressPhase.Merging => FormatMessage("merge", progress.Message),
            ProgressPhase.ExtractingAudio => FormatMessage("audio", progress.Message),
            ProgressPhase.Converting => FormatMessage("convert", progress.Message),
            ProgressPhase.EmbeddingThumbnail => FormatMessage("thumbnail", progress.Message),
            ProgressPhase.PostProcessing => FormatMessage("postprocess", progress.Message),
            _ => progress.RawLine ?? progress.Message ?? progress.Phase.ToString()
        };
    }

    private static string FormatDownloadProgress(YtDlpProgress progress, double percent)
    {
        var parts = new List<string>
        {
            "[download]",
            percent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
        };

        var bytes = FormatBytePair(progress.DownloadedBytes, progress.TotalBytes);
        if (bytes is not null)
        {
            parts.Add(bytes);
        }

        if (!string.IsNullOrWhiteSpace(progress.Speed))
        {
            parts.Add(progress.Speed);
        }

        if (progress.Eta is { } eta)
        {
            parts.Add("ETA " + FormatDuration(eta));
        }

        return string.Join("  ", parts);
    }

    private static string FormatMessage(string label, string? message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? $"[{label}]"
            : $"[{label}] {message}";
    }

    private static string? FormatBytePair(long? downloadedBytes, long? totalBytes)
    {
        return (downloadedBytes, totalBytes) switch
        {
            ({ } downloaded, { } total) => $"{FormatBytes(downloaded)} / {FormatBytes(total)}",
            ({ } downloaded, null) => FormatBytes(downloaded),
            (null, { } total) => FormatBytes(total),
            _ => null
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unitIndex]);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
