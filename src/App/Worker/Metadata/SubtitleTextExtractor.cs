using System.Text;
using System.Text.RegularExpressions;

namespace Worker.Metadata;

/// <summary>
/// Extracts plain searchable text from subtitle sidecar files. Only VTT, SRT, and ASS/SSA are
/// parsed; all other formats return null. The extracted text is stored in the DB and indexed in
/// Typesense so users can search subtitle content.
/// </summary>
internal static partial class SubtitleTextExtractor
{
    [GeneratedRegex(@"^\d{1,2}:\d{2}:\d{2}[.,]\d{3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[.,]\d{3}", RegexOptions.Compiled)]
    private static partial Regex TimestampLine();

    [GeneratedRegex(@"<\d{1,2}:\d{2}:\d{2}\.\d{3}>", RegexOptions.Compiled)]
    private static partial Regex InlineTimestamp();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^Dialogue:\s*\d+,[^,]+,[^,]+,[^,]*,[^,]*,\d+,\d+,\d+,[^,]*,(.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex AssDialogue();

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.Compiled)]
    private static partial Regex AssTag();

    public static string? ExtractText(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".vtt" or ".srt" or ".ass" or ".ssa"))
            return null;

        string content;
        try
        {
            content = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch
        {
            return null;
        }

        return ext switch
        {
            ".vtt" => ParseVtt(content),
            ".srt" => ParseSrt(content),
            ".ass" or ".ssa" => ParseAss(content),
            _ => null
        };
    }

    private static string? ParseVtt(string content)
    {
        var sb = new StringBuilder();
        var inCue = false;
        var lastLine = string.Empty;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("WEBVTT") || line.StartsWith("NOTE") ||
                line.StartsWith("REGION") || line.StartsWith("STYLE"))
            {
                inCue = false;
                continue;
            }

            if (TimestampLine().IsMatch(line))
            {
                inCue = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                inCue = false;
                continue;
            }

            if (!inCue)
                continue;

            var text = InlineTimestamp().Replace(line, "");
            text = HtmlTag().Replace(text, "").Trim();

            // YouTube VTT uses rolling cues where each cue repeats previous lines — skip duplicates.
            if (string.IsNullOrWhiteSpace(text) || text == lastLine)
                continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(text);
            lastLine = text;
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private static string? ParseSrt(string content)
    {
        var sb = new StringBuilder();
        var inText = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (TimestampLine().IsMatch(line))
            {
                inText = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                inText = false;
                continue;
            }

            if (int.TryParse(line.Trim(), out _))
            {
                inText = false;
                continue;
            }

            if (!inText)
                continue;

            var text = HtmlTag().Replace(line, "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(text);
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private static string? ParseAss(string content)
    {
        var sb = new StringBuilder();

        foreach (Match match in AssDialogue().Matches(content))
        {
            var text = AssTag().Replace(match.Groups[1].Value, "")
                .Replace("\\N", " ").Replace("\\n", " ").Trim();

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(text);
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }
}
