using Microsoft.Extensions.Options;

namespace WebAPI.Features.Media.Casting;

/// <summary>
/// Builds the absolute URLs handed to a cast device. The device fetches media over the network, so
/// every URL must use a base it can reach: <c>Cast:AdvertisedBaseUrl</c> when configured, otherwise
/// the browser origin of the request that started the session (Origin, then Referer, then the
/// request host — the frontend proxy forwards <c>?castToken=</c> requests without a session, so the
/// browser origin works for proxied deployments). Loopback bases are rejected outright because a
/// cast device can never fetch them.
/// </summary>
public sealed class CastMediaUrlBuilder(IOptions<CastingOptions> options)
{
    public (string? BaseUrl, string? Error) ResolveBaseUrl(HttpRequest request)
    {
        var candidate = FirstNonEmpty(
            options.Value.AdvertisedBaseUrl,
            request.Headers.Origin.ToString(),
            OriginOf(request.Headers.Referer.ToString()),
            $"{request.Scheme}://{request.Host}");

        if (candidate is null || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, "Could not determine a base URL for the cast device. Configure Cast:AdvertisedBaseUrl.");
        }

        if (uri.IsLoopback)
        {
            return (null,
                $"The cast base URL '{uri.GetLeftPart(UriPartial.Authority)}' is a loopback address, which a cast " +
                "device cannot reach. Configure Cast:AdvertisedBaseUrl with a LAN-reachable URL, or browse the app " +
                "via a LAN address.");
        }

        return (uri.GetLeftPart(UriPartial.Authority), null);
    }

    public static string BuildStreamUrl(string baseUrl, Guid mediaGuid, string castToken, bool audio, string? format)
        => audio
            ? $"{baseUrl}/api/watch/{mediaGuid:D}?audio=true&format={Uri.EscapeDataString(format ?? AudioRenditionHelpers.DefaultFormat)}&{TokenQuery(castToken)}"
            : $"{baseUrl}/api/watch/{mediaGuid:D}?{TokenQuery(castToken)}";

    public static string BuildThumbnailUrl(string baseUrl, Guid mediaGuid, string castToken)
        => $"{baseUrl}/api/watch/{mediaGuid:D}/thumbnail?{TokenQuery(castToken)}";

    public static string BuildCaptionUrl(
        string baseUrl,
        Guid mediaGuid,
        string languageCode,
        string? captionType,
        string castToken)
    {
        var typeQuery = captionType is null ? "" : $"captionType={Uri.EscapeDataString(captionType)}&";
        return $"{baseUrl}/api/watch/{mediaGuid:D}/captions/{Uri.EscapeDataString(languageCode)}?{typeQuery}{TokenQuery(castToken)}";
    }

    private static string TokenQuery(string castToken)
        => $"{CastTokenDefaults.QueryParameter}={Uri.EscapeDataString(castToken)}";

    private static string? OriginOf(string referer)
        => Uri.TryCreate(referer, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
