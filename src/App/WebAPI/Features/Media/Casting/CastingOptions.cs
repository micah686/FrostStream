namespace WebAPI.Features.Media.Casting;

/// <summary>
/// Server-side casting settings. Binds the same <c>Cast</c> section as
/// <see cref="CastTokenOptions"/> (unknown keys are ignored by binding), so token and device
/// settings live in one config block.
/// </summary>
public sealed class CastingOptions
{
    public const string SectionName = "Cast";

    /// <summary>
    /// Base URL (scheme://host[:port]) the cast device uses to fetch media, captions, and artwork.
    /// Must be reachable from the device's network — loopback addresses are rejected because a
    /// Chromecast can never fetch them. When unset, the URL is derived from the incoming request's
    /// Origin/Referer/host, which is correct when the frontend is browsed via a LAN address.
    /// Note the ASP.NET dev certificate is untrusted on cast devices; prefer a plain-HTTP LAN base
    /// or a trusted certificate.
    /// </summary>
    public string? AdvertisedBaseUrl { get; init; }

    /// <summary>How long a discovery result is served from cache before a new mDNS scan.</summary>
    public int DeviceCacheSeconds { get; init; } = 60;

    /// <summary>Upper bound for one progressive mDNS discovery pass.</summary>
    public int DiscoveryTimeoutSeconds { get; init; } = 5;
}
