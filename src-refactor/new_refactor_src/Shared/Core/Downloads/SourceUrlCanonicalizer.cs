using System.Text;

namespace Shared.Downloads;

/// <summary>
/// Canonicalizes creator-source URLs so URL spelling variants of the same channel
/// (<c>https://www.youtube.com/@Name</c>, <c>https://youtube.com/@Name/videos</c>, trailing
/// slash, tracking params, …) map to one <c>downloads.creator_sources</c> row. The repository
/// dedupes by exact string equality on <c>source_url</c>, so every writer must canonicalize
/// before persisting or looking up. Fix-forward: rows stored before canonicalization keep
/// their raw URL and will not match canonical input.
/// </summary>
public static class SourceUrlCanonicalizer
{
    /// <summary>Hosts safe to upgrade http → https. Never upgrade arbitrary hosts — self-hosted http-only sites must keep working.</summary>
    private static readonly HashSet<string> HttpsUpgradeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "youtu.be", "twitch.tv", "vimeo.com", "music.youtube.com"
    };

    private static readonly HashSet<string> YouTubeChannelTabSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "videos", "shorts", "streams", "featured", "playlists"
    };

    /// <summary>Query params that never affect what a URL points at.</summary>
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "si", "feature", "fbclid", "gclid", "pp", "ab_channel"
    };

    public static string Canonicalize(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return trimmed;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        if (host == "m.youtube.com")
            host = "youtube.com";

        var scheme = uri.Scheme == Uri.UriSchemeHttp && HttpsUpgradeHosts.Contains(host)
            ? Uri.UriSchemeHttps
            : uri.Scheme.ToLowerInvariant();

        var path = CanonicalizePath(host, uri.AbsolutePath);
        var query = CanonicalizeQuery(uri.Query);

        var builder = new StringBuilder(scheme).Append("://").Append(host);
        if (!uri.IsDefaultPort && !(scheme == Uri.UriSchemeHttps && uri.Port == 443))
            builder.Append(':').Append(uri.Port);
        builder.Append(path);
        if (query.Length > 0)
            builder.Append('?').Append(query);
        return builder.ToString();
    }

    private static string CanonicalizePath(string host, string path)
    {
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        if (host is "youtube.com" or "music.youtube.com")
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var isChannelPath = segments.Length > 0
                && (segments[0].StartsWith('@')
                    || segments[0] is "channel" or "c" or "user");
            if (isChannelPath
                && segments.Length > 1
                && YouTubeChannelTabSegments.Contains(segments[^1]))
            {
                path = "/" + string.Join('/', segments[..^1]);
            }
        }

        return path == "/" ? string.Empty : path;
    }

    private static string CanonicalizeQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
            return string.Empty;

        var survivors = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair =>
            {
                var name = pair.Split('=', 2)[0];
                return name.Length > 0
                       && !TrackingParams.Contains(name)
                       && !name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(pair => pair, StringComparer.Ordinal)
            .ToArray();

        return string.Join('&', survivors);
    }
}
