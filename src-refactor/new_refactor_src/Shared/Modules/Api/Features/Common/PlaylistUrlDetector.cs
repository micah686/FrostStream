namespace WebAPI.Features.Common;

/// <summary>
/// Conservative playlist-container detection for the direct download endpoint. Only URLs that
/// unambiguously reference a playlist container (no individual video) are matched, so they can
/// be auto-routed into the playlist pipeline with proper fan-out and per-entry tracking.
/// Policy: <c>watch?v=X&amp;list=Y</c> and <c>youtu.be/X?list=Y</c> reference a specific video
/// inside a playlist and stay on the direct path as a single-video download.
/// Currently YouTube-only; other platforms always go direct.
/// </summary>
public static class PlaylistUrlDetector
{
    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com"
    };

    public static bool IsPlaylistUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || !YouTubeHosts.Contains(uri.Host))
        {
            return false;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var hasList = !string.IsNullOrWhiteSpace(query["list"]);
        if (!hasList)
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Equals("/playlist", StringComparison.OrdinalIgnoreCase))
            return true;

        // A /watch URL with list= but no v= is a container reference, not a specific video.
        return path.Equals("/watch", StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrWhiteSpace(query["v"]);
    }
}
