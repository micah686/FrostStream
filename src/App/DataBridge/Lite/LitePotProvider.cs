using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YtDlpSharpLib.Options;

namespace DataBridge.Lite;

public sealed record LitePotResponse(HttpStatusCode StatusCode, string? ContentType, byte[] Body);

public interface ILitePotProvider
{
    bool Enabled { get; }
    Task EnsureAvailableAsync(CancellationToken cancellationToken = default);
    Task<LitePotResponse> ForwardAsync(
        HttpMethod method,
        string pathAndQuery,
        Stream? body,
        string? contentType,
        CancellationToken cancellationToken = default);
    YtDlpOptions Apply(YtDlpOptions options);
}

/// <summary>Direct in-process bgutil client used by Lite; no broker, NATS request, or remote worker is involved.</summary>
public sealed class LitePotProvider(
    HttpClient httpClient,
    IOptions<LitePotOptions> options,
    ILogger<LitePotProvider> logger) : ILitePotProvider
{
    private readonly LitePotOptions _options = options.Value;
    public bool Enabled => _options.Enabled;

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return;
        var provider = ProviderBase();
        try
        {
            using var response = await httpClient.GetAsync(new Uri(provider, "ping"), cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"bgutil POT provider returned HTTP {(int)response.StatusCode} from /ping.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The configured Lite bgutil POT provider is unavailable.");
            throw new InvalidOperationException(
                "POT is enabled, but the bgutil provider is unavailable. Check LitePot:ProviderUrl and the provider container health, or disable LitePot:Enabled.",
                exception);
        }
    }

    public async Task<LitePotResponse> ForwardAsync(
        HttpMethod method,
        string pathAndQuery,
        Stream? body,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("POT support is disabled.");
        if (pathAndQuery.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("POT provider paths cannot contain parent traversal.", nameof(pathAndQuery));

        using var request = new HttpRequestMessage(method, new Uri(ProviderBase(), pathAndQuery.TrimStart('/')));
        if (body is not null)
        {
            request.Content = new StreamContent(body);
            if (!string.IsNullOrWhiteSpace(contentType))
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return new LitePotResponse(response.StatusCode, response.Content.Headers.ContentType?.ToString(),
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public YtDlpOptions Apply(YtDlpOptions options)
    {
        if (!Enabled)
            return options;
        var extractorArgs = new List<string>(options.Extractor.ExtractorArgs)
        {
            $"youtubepot-bgutilhttp:base_url={_options.ProxyBaseUrl.TrimEnd('/')}"
        };
        var pluginDirs = new List<string>(options.General.PluginDirs);
        var directory = string.IsNullOrWhiteSpace(_options.PluginDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp-plugins")
            : Path.GetFullPath(_options.PluginDirectory);
        if (!pluginDirs.Contains(directory, StringComparer.Ordinal)) pluginDirs.Add(directory);
        if (!pluginDirs.Contains("default", StringComparer.Ordinal)) pluginDirs.Add("default");
        return options with
        {
            Extractor = options.Extractor with { ExtractorArgs = extractorArgs },
            General = options.General with { PluginDirs = pluginDirs }
        };
    }

    private Uri ProviderBase()
    {
        if (string.IsNullOrWhiteSpace(_options.ProviderUrl)
            || !Uri.TryCreate(_options.ProviderUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("LitePot:ProviderUrl must be an absolute HTTP(S) URL when POT is enabled.");
        return uri;
    }
}
