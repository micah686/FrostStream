using Microsoft.Extensions.Logging;
using Shared.Secrets;

namespace Worker.Services;

/// <summary>
/// Reads a Netscape-formatted cookie file from a user-owned OpenBAO profile path
/// (<c>cookies/users/{subject}/{profileKey}</c>) and materializes it into a temp file the worker
/// passes to yt-dlp's <c>--cookies</c> flag. Implements <see cref="IAsyncDisposable"/> so the file
/// is removed after the run.
/// </summary>
public sealed class CookieMaterializer : IAsyncDisposable
{
    private const string CookieField = "content";

    private readonly string? _path;
    private readonly ILogger _logger;

    private CookieMaterializer(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Absolute path of the materialized cookie file, or <see langword="null"/> when no cookie was used.</summary>
    public string? FilePath => _path;

    public static async Task<CookieMaterializer> CreateFromPathAsync(
        ISecretStore secretStore,
        string? secretPath,
        string scratchDirectory,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretPath))
            return new CookieMaterializer(path: null, logger);

        var secrets = await secretStore.ReadAsync(secretPath, cancellationToken);
        if (secrets is null || !secrets.TryGetValue(CookieField, out var content) || string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException(
                $"No cookie content was found in OpenBAO at '{secretPath}'. " +
                $"Expected a '{CookieField}' field with the Netscape cookie text.");
        }

        Directory.CreateDirectory(scratchDirectory);
        var path = Path.Combine(scratchDirectory, $"cookies-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, content, cancellationToken);

        return new CookieMaterializer(path, logger);
    }

    public ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(_path))
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temp cookie file at {Path}", _path);
            }
        }

        return ValueTask.CompletedTask;
    }
}
