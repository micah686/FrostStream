using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaProcessor.Storage;

/// <summary>
/// Moves media bytes through WebAPI's internal storage endpoints. MediaProcessor keeps only local
/// scratch files and never needs FluentStorage credentials, OpenBao, or a shared storage mount.
/// </summary>
public sealed class MediaProcessorStorageClient(
    HttpClient httpClient,
    IOptions<MediaProcessorOptions> options,
    ILogger<MediaProcessorStorageClient> logger)
{
    private const string ApiKeyHeader = "X-FrostStream-MediaProcessor-Key";

    public async Task DownloadToFileAsync(
        string storageKey,
        string storagePath,
        string localPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildBlobUri(storageKey, storagePath));
        AddApiKey(request);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FileNotFoundException($"Source blob '{storagePath}' was not found in storage '{storageKey}'.", storagePath);

        await EnsureSuccessAsync(response, $"Downloading '{storageKey}:{storagePath}' failed.", cancellationToken);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(localPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    public async Task UploadFromFileAsync(
        string localPath,
        string storageKey,
        string storagePath,
        CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, BuildBlobUri(storageKey, storagePath))
        {
            Content = content
        };
        AddApiKey(request);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await EnsureSuccessAsync(response, $"Uploading '{storageKey}:{storagePath}' failed.", cancellationToken);
    }

    private Uri BuildBlobUri(string storageKey, string storagePath)
    {
        if (string.IsNullOrWhiteSpace(options.Value.WebApiBaseUrl))
            throw new InvalidOperationException("MediaProcessor:WebApiBaseUrl must be configured.");

        var baseUri = new Uri(options.Value.WebApiBaseUrl.TrimEnd('/') + "/");
        var escapedPath = string.Join(
            '/',
            storagePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        return new Uri(baseUri, $"api/internal/media-storage/{Uri.EscapeDataString(storageKey)}/{escapedPath}");
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("MediaProcessor:ApiKey must be configured.");

        request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string message, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning(
            "Media storage API returned {StatusCode}: {Detail}",
            (int)response.StatusCode,
            string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail}");
    }
}
