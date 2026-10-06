namespace Worker.Services;

/// <summary>
/// Holds the loopback base URL of the running POT shim (e.g. <c>http://127.0.0.1:54321</c>), or
/// <see langword="null"/> when POT is disabled. Set once by <see cref="PotShimService"/> at startup and
/// read by the download path when building the bgutil <c>base_url</c> extractor-arg.
/// </summary>
public sealed class PotShimEndpoint
{
    private volatile string? _baseUrl;

    /// <summary>The shim's base URL without a trailing slash, or null when POT is disabled / not started.</summary>
    public string? BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = value;
    }
}
