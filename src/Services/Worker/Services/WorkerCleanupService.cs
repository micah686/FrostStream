using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Worker.Services;

/// <summary>
/// Hosted service that cleans up stale temporary directories on worker startup.
/// Handles crash recovery by removing old temp files that weren't cleaned up properly.
/// </summary>
public class WorkerCleanupService : IHostedService
{
    private readonly ILogger<WorkerCleanupService> _logger;
    private readonly TimeSpan _cleanupThreshold;

    public WorkerCleanupService(
        ILogger<WorkerCleanupService> logger,
        TimeSpan? cleanupThreshold = null)
    {
        _logger = logger;
        _cleanupThreshold = cleanupThreshold ?? TimeSpan.FromHours(1);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "froststream", "data");

        if (!Directory.Exists(tempDir))
        {
            _logger.LogDebug("Temp directory does not exist: {Dir}", tempDir);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting temp directory cleanup in: {Dir}", tempDir);

        try
        {
            var oldDirs = Directory.GetDirectories(tempDir)
                .Where(d => Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.Subtract(_cleanupThreshold))
                .ToList();

            if (oldDirs.Count == 0)
            {
                _logger.LogDebug("No old temp directories found for cleanup");
                return Task.CompletedTask;
            }

            _logger.LogInformation("Found {Count} old temp directories to clean up", oldDirs.Count);

            foreach (var dir in oldDirs)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("Cleaned up old temp directory: {Dir}", dir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temp directory: {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during temp directory cleanup");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup on stop is handled by the FileProcessHandler's normal flow
        return Task.CompletedTask;
    }
}
