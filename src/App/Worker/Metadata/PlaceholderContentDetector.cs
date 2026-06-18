using YtDlpSharpLib.Models;

namespace Worker.Metadata;

internal static class PlaceholderContentDetector
{
    public const string ErrorCode = "yt-dlp.placeholder-content";

    private const string AlternativeClientPlaceholderId = "aQvGIIdgFDM";

    private static readonly string[] PlaceholderTitleMarkers =
    [
        "content is not available on this app",
        "not available on this app",
        "video is not available on this app"
    ];

    private static readonly HashSet<string> PlaceholderContentHashes = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsPlaceholderMetadata(VideoInfo info, string? provider = null)
    {
        if (!IsYouTubeProvider(provider) && !IsYouTubeProvider(info.Extractor) && !IsYouTubeProvider(info.ExtractorKey))
            return false;

        return IsAlternativeClientPlaceholderId(info.Id)
               || IsAlternativeClientPlaceholderId(info.DisplayId)
               || ContainsPlaceholderTitleMarker(info.Title)
               || ContainsPlaceholderTitleMarker(info.FullTitle)
               || ContainsPlaceholderTitleMarker(info.AltTitle);
    }

    public static void ThrowIfPlaceholderMetadata(VideoInfo info, string? provider = null)
    {
        if (!IsPlaceholderMetadata(info, provider))
            return;

        throw new YtDlpPlaceholderContentException(
            "yt-dlp returned a known YouTube alternative-client placeholder instead of the requested source content.");
    }

    public static bool IsPlaceholderContentHash(
        string? contentHashXxh128,
        IReadOnlySet<string>? placeholderContentHashes = null)
    {
        if (string.IsNullOrWhiteSpace(contentHashXxh128))
            return false;

        var hashes = placeholderContentHashes ?? PlaceholderContentHashes;
        return hashes.Contains(contentHashXxh128.Trim());
    }

    public static void ThrowIfPlaceholderContentHash(string? contentHashXxh128)
    {
        if (!IsPlaceholderContentHash(contentHashXxh128))
            return;

        throw new YtDlpPlaceholderContentException(
            "yt-dlp downloaded bytes matching a known YouTube alternative-client placeholder.");
    }

    private static bool IsYouTubeProvider(string? provider)
        => !string.IsNullOrWhiteSpace(provider)
           && provider.Contains("youtube", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlternativeClientPlaceholderId(string? id)
        => string.Equals(id?.Trim(), AlternativeClientPlaceholderId, StringComparison.Ordinal);

    private static bool ContainsPlaceholderTitleMarker(string? title)
        => !string.IsNullOrWhiteSpace(title)
           && PlaceholderTitleMarkers.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
