using Microsoft.Extensions.Logging;
using Shared.Secrets;

namespace DataBridge.Lite;

internal sealed class LiteCookieMaterializer : IAsyncDisposable
{
    private readonly string? _path;
    private readonly ILogger _logger;
    private LiteCookieMaterializer(string? path, ILogger logger) => (_path, _logger) = (path, logger);
    public string? FilePath => _path;

    public static async Task<LiteCookieMaterializer> CreateAsync(
        ISecretStore store, string? secretPath, string scratchPath, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secretPath))
            return new LiteCookieMaterializer(null, logger);
        var document = await store.ReadAsync(secretPath, cancellationToken);
        if (document is null || !document.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("The selected cookie profile has no Netscape cookie content.");
        Directory.CreateDirectory(scratchPath);
        var path = Path.Combine(scratchPath, $"cookies-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, content, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new LiteCookieMaterializer(path, logger);
    }

    public ValueTask DisposeAsync()
    {
        if (_path is null) return ValueTask.CompletedTask;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception exception) { _logger.LogWarning(exception, "Could not remove the temporary cookie file."); }
        return ValueTask.CompletedTask;
    }
}
