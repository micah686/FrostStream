namespace Shared.Metadata;

/// <summary>
/// Canonical derivation of creator identity for <c>metadata.accounts</c>. Every writer of
/// that table — the media-download metadata write and the channel asset refresh — must derive
/// <c>platform</c> and <c>account_handle</c> through these helpers, otherwise the
/// <c>(platform, account_handle)</c> upsert key drifts between paths and the same creator
/// splits into multiple rows (e.g. <c>youtube</c> vs <c>youtube:tab</c>).
/// </summary>
public static class CreatorIdentity
{
    /// <summary>
    /// Normalizes a yt-dlp extractor name into the platform value stored on
    /// <c>metadata.accounts</c>: first non-blank candidate, trimmed, lowercased, with any
    /// <c>:sub-extractor</c> suffix removed (<c>youtube:tab</c> → <c>youtube</c>).
    /// Returns null when all candidates are blank.
    /// Do NOT use for <c>downloads.discovered_media</c> keys — those store raw extractor
    /// values that also feed the deterministic JobId hash.
    /// </summary>
    public static string? NormalizePlatform(params string?[] extractorCandidates)
    {
        var value = FirstNonBlank(extractorCandidates);
        if (value is null)
            return null;

        var colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[..colon].Trim();

        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    public static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
