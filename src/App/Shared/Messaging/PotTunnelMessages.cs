namespace Shared.Messaging;

/// <summary>
/// A bgutil provider HTTP call tunneled over NATS. The Worker shim captures the request the yt-dlp
/// plugin made and forwards it verbatim; a broker replays it against its nearby provider. Kept
/// schema-agnostic (just method/path/body) so it survives bgutil API changes without C# changes.
/// </summary>
public sealed record PotTunnelRequest
{
    /// <summary>HTTP method the plugin used (e.g. <c>GET</c>, <c>POST</c>).</summary>
    public required string Method { get; init; }

    /// <summary>Path and query the plugin requested (e.g. <c>/get_pot</c>, <c>/ping</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Request body as sent by the plugin (JSON for <c>/get_pot</c>), or <see langword="null"/>.</summary>
    public string? Body { get; init; }

    /// <summary>Content type of <see cref="Body"/>, when present.</summary>
    public string? ContentType { get; init; }
}

/// <summary>The provider's HTTP response, tunneled back to the Worker shim.</summary>
public sealed record PotTunnelResponse
{
    /// <summary>HTTP status code returned by the provider, or a gateway code (502) on broker failure.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Response body to relay back to the yt-dlp plugin.</summary>
    public string? Body { get; init; }

    /// <summary>Content type of <see cref="Body"/>, when known.</summary>
    public string? ContentType { get; init; }
}
