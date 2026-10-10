namespace WebAPI.Features.Media;

/// <summary>
/// CORS policy for endpoints that cast devices fetch directly (progressive stream, captions, HLS
/// manifests and segments). Cast receivers load media from a different origin than the sender page,
/// so these read-only, token/authorization-gated endpoints allow any origin.
/// </summary>
public static class MediaCors
{
    public const string Policy = "media-cast";
}
