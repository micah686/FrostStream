using System.Buffers;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Worker.Services;

public sealed class AssetCacheWriter(
    IHttpClientFactory httpClientFactory,
    IOptions<AssetCacheOptions> options,
    ILogger<AssetCacheWriter> logger)
{
    private const string HttpClientName = "asset-cache";
    private readonly AssetCacheOptions _options = options.Value;

    public async Task<AssetDownloadResult> DownloadAndStoreAsync(
        string url,
        AssetKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        var delay = _options.InitialBackoff > TimeSpan.Zero
            ? _options.InitialBackoff
            : TimeSpan.FromMilliseconds(250);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await DownloadOnceAsync(url, kind, attempt, cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                logger.LogWarning(ex, "Asset download failed on attempt {Attempt} for {Url}; retrying in {Delay}ms", attempt, url, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }

        throw lastError ?? new InvalidOperationException($"Asset download failed for '{url}'.");
    }

    private async Task<AssetDownloadResult> DownloadOnceAsync(
        string url,
        AssetKind kind,
        int attempt,
        CancellationToken cancellationToken)
    {
        var root = ResolveRoot();
        var tempDir = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long contentLength = 0;
        try
        {
            await using (var network = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                int read;
                while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    hasher.Append(buffer.AsSpan(0, read));
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    contentLength += read;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Span<byte> hashBytes = stackalloc byte[16];
        hasher.GetCurrentHash(hashBytes);
        var contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var extension = ResolveExtension(url, response.Content.Headers.ContentType?.MediaType);
        var kindFolder = kind switch
        {
            AssetKind.Avatar => "avatars",
            AssetKind.Banner => "banners",
            _ => "other"
        };

        var shardA = contentHash[..2];
        var shardB = contentHash.Substring(2, 2);
        var finalDir = Path.Combine(root, kindFolder, shardA, shardB);
        Directory.CreateDirectory(finalDir);
        var finalPath = Path.Combine(finalDir, $"{contentHash}{extension}");

        if (File.Exists(finalPath))
        {
            TryDelete(tempPath);
            logger.LogInformation("Asset cache hit for {Url} → {FinalPath} (hash {Hash})", url, finalPath, contentHash);
            return new AssetDownloadResult(finalPath, contentHash, contentLength, extension, attempt, ReusedExisting: true);
        }

        try
        {
            File.Move(tempPath, finalPath);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            TryDelete(tempPath);
        }

        logger.LogInformation("Asset stored for {Url} → {FinalPath} (hash {Hash}, {Bytes} bytes)", url, finalPath, contentHash, contentLength);
        return new AssetDownloadResult(finalPath, contentHash, contentLength, extension, attempt, ReusedExisting: false);
    }

    private string ResolveRoot()
    {
        var root = string.IsNullOrWhiteSpace(_options.Root) ? ".cache" : _options.Root;
        if (Path.IsPathRooted(root))
        {
            return root;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));
    }

    private static string ResolveExtension(string url, string? contentType)
    {
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length is > 1 and <= 6)
            {
                return ext.ToLowerInvariant();
            }
        }
        catch
        {
            // fall through to content-type
        }

        return contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".bin"
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

public sealed record AssetDownloadResult(
    string CachePath,
    string ContentHash,
    long ContentLength,
    string Extension,
    int Attempts,
    bool ReusedExisting);
