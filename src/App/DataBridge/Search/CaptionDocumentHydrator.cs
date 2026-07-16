using System.Text;
using System.Text.RegularExpressions;
using FluentStorage.Blobs;
using Microsoft.Extensions.Logging;
using Shared.Storage;

namespace DataBridge.Search;

/// <summary>
/// Builds the search-only caption text from the durable sidecar. PostgreSQL deliberately stores
/// only the track locator; Typesense remains the sole indexed copy of transcript text.
/// </summary>
public sealed partial class CaptionDocumentHydrator(
    IBlobStorageProvider blobStorageProvider,
    ILogger<CaptionDocumentHydrator> logger)
{
    private const int MaximumCaptionBytes = 16 * 1024 * 1024;

    [GeneratedRegex(@"^\d{1,2}:\d{2}:\d{2}[.,]\d{3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[.,]\d{3}", RegexOptions.Compiled)]
    private static partial Regex TimestampLine();

    [GeneratedRegex(@"<\d{1,2}:\d{2}:\d{2}\.\d{3}>", RegexOptions.Compiled)]
    private static partial Regex InlineTimestamp();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^Dialogue:\s*\d+,[^,]+,[^,]+,[^,]*,[^,]*,\d+,\d+,\d+,[^,]*,(.*)$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex AssDialogue();

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.Compiled)]
    private static partial Regex AssTag();

    public async Task<IReadOnlyList<CaptionDocument>> HydrateAsync(
        IReadOnlyList<CaptionDocument> captions,
        CancellationToken ct = default)
    {
        if (captions.Count == 0)
            return captions;

        return await Task.WhenAll(captions.Select(caption => HydrateAsync(caption, ct)));
    }

    private async Task<CaptionDocument> HydrateAsync(CaptionDocument caption, CancellationToken ct)
    {
        var extension = Path.GetExtension(caption.StoragePath).ToLowerInvariant();
        if (extension is not (".vtt" or ".srt" or ".ass" or ".ssa"))
            return caption;

        try
        {
            var storage = await blobStorageProvider.GetAsync(caption.StorageKey, ct);
            await using var stream = await storage.OpenReadAsync(caption.StoragePath, ct);
            if (stream is null)
            {
                logger.LogWarning("Caption sidecar was missing while indexing {StorageKey}:{StoragePath}.", caption.StorageKey, caption.StoragePath);
                return caption;
            }

            if (stream.CanSeek && stream.Length > MaximumCaptionBytes)
            {
                logger.LogWarning("Skipping oversized caption sidecar {StorageKey}:{StoragePath}.", caption.StorageKey, caption.StoragePath);
                return caption;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: false);
            var content = await reader.ReadToEndAsync(ct);
            if (Encoding.UTF8.GetByteCount(content) > MaximumCaptionBytes)
            {
                logger.LogWarning("Skipping oversized caption sidecar {StorageKey}:{StoragePath}.", caption.StorageKey, caption.StoragePath);
                return caption;
            }

            return caption with { Text = ExtractText(content, extension) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Unable to read caption sidecar for Typesense indexing {StorageKey}:{StoragePath}.", caption.StorageKey, caption.StoragePath);
            return caption;
        }
    }

    private static string? ExtractText(string content, string extension)
        => extension switch
        {
            ".vtt" => ParseVtt(content),
            ".srt" => ParseSrt(content),
            ".ass" or ".ssa" => ParseAss(content),
            _ => null
        };

    private static string? ParseVtt(string content)
    {
        var text = new StringBuilder();
        var inCue = false;
        var lastLine = string.Empty;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("WEBVTT") || line.StartsWith("NOTE") || line.StartsWith("REGION") || line.StartsWith("STYLE")) { inCue = false; continue; }
            if (TimestampLine().IsMatch(line)) { inCue = true; continue; }
            if (string.IsNullOrWhiteSpace(line)) { inCue = false; continue; }
            if (!inCue) continue;
            var cue = HtmlTag().Replace(InlineTimestamp().Replace(line, ""), "").Trim();
            if (string.IsNullOrWhiteSpace(cue) || cue == lastLine) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(cue);
            lastLine = cue;
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static string? ParseSrt(string content)
    {
        var text = new StringBuilder();
        var inText = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (TimestampLine().IsMatch(line)) { inText = true; continue; }
            if (string.IsNullOrWhiteSpace(line)) { inText = false; continue; }
            if (int.TryParse(line.Trim(), out _)) { inText = false; continue; }
            if (!inText) continue;
            var cue = HtmlTag().Replace(line, "").Trim();
            if (string.IsNullOrWhiteSpace(cue)) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(cue);
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static string? ParseAss(string content)
    {
        var text = new StringBuilder();
        foreach (Match match in AssDialogue().Matches(content))
        {
            var cue = AssTag().Replace(match.Groups[1].Value, "").Replace("\\N", " ").Replace("\\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(cue)) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(cue);
        }
        return text.Length == 0 ? null : text.ToString();
    }
}
