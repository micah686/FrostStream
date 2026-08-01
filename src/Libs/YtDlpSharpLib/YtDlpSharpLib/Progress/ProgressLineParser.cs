using System.Globalization;

namespace YtDlpSharpLib.Progress;

/// <summary>
/// Allocation-conscious parser for yt-dlp progress lines. Operates on
/// <see cref="ReadOnlySpan{T}"/> input so callers can avoid per-line string copies
/// for the percent/speed/ETA path.
/// </summary>
public static class ProgressLineParser
{
    private const string OfSentinel = " of ";
    private const string AtSentinel = " at ";
    private const string EtaSentinel = " ETA ";
    private const string InSentinel = " in ";
    private const string DestinationSentinel = "Destination:";
    private const string AlreadyDownloadedSentinel = "has already been downloaded";

    /// <summary>
    /// Attempts to parse a single line of yt-dlp output into a <see cref="YtDlpProgress"/>.
    /// Returns <see langword="false"/> for lines that cannot be classified as progress.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> line, out YtDlpProgress progress)
    {
        var trimmed = line.TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '[')
        {
            progress = null!;
            return false;
        }

        var closeBracket = trimmed.IndexOf(']');
        if (closeBracket < 0)
        {
            progress = null!;
            return false;
        }

        var tag = trimmed[1..closeBracket];
        var rest = trimmed[(closeBracket + 1)..].TrimStart();
        var phase = MapPhase(tag);
        var rawLine = line.ToString();

        if (phase == ProgressPhase.Downloading)
        {
            progress = ParseDownload(rest, rawLine);
            return true;
        }

        var restString = rest.IsEmpty ? null : rest.ToString();

        progress = new YtDlpProgress
        {
            Phase = phase,
            AdditionalInfo = restString,
            Message = restString,
            Destination = ExtractDestination(restString),
            RawLine = rawLine
        };
        return true;
    }

    /// <summary>
    /// Attempts to parse a single line of yt-dlp output into a <see cref="YtDlpProgress"/>.
    /// </summary>
    public static bool TryParse(string line, out YtDlpProgress progress)
    {
        ArgumentNullException.ThrowIfNull(line);
        return TryParse(line.AsSpan(), out progress);
    }

    /// <summary>
    /// Parses a single line of yt-dlp output, returning a fallback <see cref="ProgressPhase.Unknown"/>
    /// event for lines that could not be classified.
    /// </summary>
    public static YtDlpProgress Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (TryParse(line.AsSpan(), out var progress))
        {
            return progress;
        }

        return new YtDlpProgress
        {
            Phase = ProgressPhase.Unknown,
            Message = line,
            RawLine = line
        };
    }

    private static ProgressPhase MapPhase(ReadOnlySpan<char> tag) =>
        tag switch
        {
            "download" => ProgressPhase.Downloading,
            "ffmpeg" => ProgressPhase.Merging,
            "Merger" => ProgressPhase.Merging,
            "ExtractAudio" => ProgressPhase.ExtractingAudio,
            "VideoConvertor" => ProgressPhase.Converting,
            "VideoConverter" => ProgressPhase.Converting,
            "EmbedThumbnail" => ProgressPhase.EmbeddingThumbnail,
            "Metadata" => ProgressPhase.PostProcessing,
            "Fixup" => ProgressPhase.PostProcessing,
            _ => ProgressPhase.PostProcessing
        };

    private static YtDlpProgress ParseDownload(ReadOnlySpan<char> rest, string rawLine)
    {
        var restString = rest.IsEmpty ? null : rest.ToString();

        if (rest.StartsWith(DestinationSentinel.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return new YtDlpProgress
            {
                Phase = ProgressPhase.Downloading,
                Message = restString,
                Destination = ExtractDestination(restString),
                AdditionalInfo = restString,
                RawLine = rawLine
            };
        }

        if (rest.IndexOf(AlreadyDownloadedSentinel.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new YtDlpProgress
            {
                Phase = ProgressPhase.Finished,
                Percent = 100,
                Message = restString,
                AdditionalInfo = restString,
                Destination = ExtractAlreadyDownloadedPath(restString),
                RawLine = rawLine
            };
        }

        if (TryParseDownloadProgress(rest, rawLine, out var parsed))
        {
            return parsed;
        }

        return new YtDlpProgress
        {
            Phase = ProgressPhase.Downloading,
            Message = restString,
            AdditionalInfo = restString,
            RawLine = rawLine
        };
    }

    private static bool TryParseDownloadProgress(
        ReadOnlySpan<char> rest,
        string rawLine,
        out YtDlpProgress progress)
    {
        progress = null!;

        var percentEnd = rest.IndexOf('%');
        if (percentEnd <= 0)
        {
            return false;
        }

        var percentStart = percentEnd;
        while (percentStart > 0)
        {
            var ch = rest[percentStart - 1];
            if (!char.IsDigit(ch) && ch != '.')
            {
                break;
            }

            percentStart--;
        }

        var percentSpan = rest[percentStart..percentEnd];
        if (!double.TryParse(percentSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return false;
        }

        long? totalBytes = null;
        string? speed = null;
        TimeSpan? eta = null;

        var ofIndex = rest.IndexOf(OfSentinel.AsSpan());
        if (ofIndex >= 0)
        {
            var sizeStart = ofIndex + OfSentinel.Length;
            while (sizeStart < rest.Length && rest[sizeStart] == '~')
            {
                sizeStart++;
            }

            var sizeSpan = ExtractToken(rest, sizeStart);
            totalBytes = ParseSize(sizeSpan);
        }

        var atIndex = rest.IndexOf(AtSentinel.AsSpan());
        if (atIndex >= 0)
        {
            var speedSpan = ExtractToken(rest, atIndex + AtSentinel.Length);
            if (!speedSpan.IsEmpty && !speedSpan.SequenceEqual("Unknown".AsSpan()))
            {
                speed = speedSpan.ToString();
            }
        }

        var etaIndex = rest.IndexOf(EtaSentinel.AsSpan());
        if (etaIndex >= 0)
        {
            var etaSpan = ExtractToken(rest, etaIndex + EtaSentinel.Length);
            if (!etaSpan.IsEmpty && !etaSpan.SequenceEqual("Unknown".AsSpan()))
            {
                eta = ParseEta(etaSpan);
            }
        }
        else
        {
            var inIndex = rest.IndexOf(InSentinel.AsSpan());
            if (inIndex >= 0)
            {
                var elapsedSpan = ExtractToken(rest, inIndex + InSentinel.Length);
                if (!elapsedSpan.IsEmpty)
                {
                    eta = ParseEta(elapsedSpan);
                }
            }
        }

        long? downloadedBytes = null;
        if (totalBytes is { } total && percent >= 0)
        {
            downloadedBytes = (long)(total * (percent / 100.0));
        }

        var phase = percent >= 100 ? ProgressPhase.Finished : ProgressPhase.Downloading;
        var message = rest.IsEmpty ? null : rest.ToString();

        progress = new YtDlpProgress
        {
            Phase = phase,
            Percent = percent,
            TotalBytes = totalBytes,
            DownloadedBytes = downloadedBytes,
            Speed = speed,
            Eta = eta,
            Message = message,
            RawLine = rawLine
        };
        return true;
    }

    private static ReadOnlySpan<char> ExtractToken(ReadOnlySpan<char> source, int start)
    {
        if (start >= source.Length)
        {
            return ReadOnlySpan<char>.Empty;
        }

        var slice = source[start..];
        var end = slice.IndexOf(' ');
        return end < 0 ? slice : slice[..end];
    }

    private static long? ParseSize(ReadOnlySpan<char> sizeSpan)
    {
        if (sizeSpan.IsEmpty || sizeSpan.SequenceEqual("N/A".AsSpan()))
        {
            return null;
        }

        var splitIdx = 0;
        while (splitIdx < sizeSpan.Length && (char.IsDigit(sizeSpan[splitIdx]) || sizeSpan[splitIdx] == '.'))
        {
            splitIdx++;
        }

        if (splitIdx == 0)
        {
            return null;
        }

        var numSpan = sizeSpan[..splitIdx];
        var unitSpan = sizeSpan[splitIdx..];

        if (!double.TryParse(numSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            return null;
        }

        var multiplier = unitSpan switch
        {
            "" or "B" => 1L,
            "KiB" => 1L << 10,
            "MiB" => 1L << 20,
            "GiB" => 1L << 30,
            "TiB" => 1L << 40,
            "K" or "KB" => 1_000L,
            "M" or "MB" => 1_000_000L,
            "G" or "GB" => 1_000_000_000L,
            "T" or "TB" => 1_000_000_000_000L,
            _ => 0L
        };

        return multiplier == 0 ? null : (long)(num * multiplier);
    }

    private static TimeSpan? ParseEta(ReadOnlySpan<char> eta)
    {
        Span<Range> ranges = stackalloc Range[3];
        var count = eta.Split(ranges, ':');

        if (count == 2
            && int.TryParse(eta[ranges[0]], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            && int.TryParse(eta[ranges[1]], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return new TimeSpan(0, minutes, seconds);
        }

        if (count == 3
            && int.TryParse(eta[ranges[0]], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(eta[ranges[1]], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
            && int.TryParse(eta[ranges[2]], NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
        {
            return new TimeSpan(hours, minutes, seconds);
        }

        return null;
    }

    private static string? ExtractDestination(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var index = message.IndexOf(DestinationSentinel, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var raw = message[(index + DestinationSentinel.Length)..].Trim();
        return raw.Trim('"');
    }

    private static string? ExtractAlreadyDownloadedPath(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var index = message.IndexOf(AlreadyDownloadedSentinel, StringComparison.OrdinalIgnoreCase);
        if (index <= 0)
        {
            return null;
        }

        return message[..index].Trim();
    }
}
