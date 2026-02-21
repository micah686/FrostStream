using DataBridge.Data;
using FluentStorage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Shared.Messages;
using Shared.Storage;

namespace DataBridge.Services;

/// <summary>
/// Background service that periodically scans for orphaned jobs — jobs stuck in
/// a non-terminal state (e.g., "Processing", "Downloading") for more than a
/// configurable threshold (default 72 hours).
///
/// For each orphaned job, it:
///   1. Attempts to locate and delete any file that was uploaded to storage
///      at the expected path (reconciliation).
///   2. Marks the job status as "Failed" with an appropriate error message.
/// </summary>
public class OrphanSweeperService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanSweeperService> _logger;

    /// <summary>How often the sweeper runs.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>How long a job can be stuck before it's considered orphaned.</summary>
    private static readonly TimeSpan OrphanThreshold = TimeSpan.FromHours(72);

    /// <summary>Non-terminal statuses that indicate potentially orphaned work.</summary>
    private static readonly string[] StuckStatuses = ["Processing", "Downloading"];

    private readonly ResiliencePipeline _resiliencePipeline;

    public OrphanSweeperService(
        IServiceScopeFactory scopeFactory,
        ILogger<OrphanSweeperService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(5),
                ShouldHandle = new PredicateBuilder()
                    .Handle<IOException>()
                    .Handle<TimeoutException>()
                    .Handle<HttpRequestException>()
            })
            .AddTimeout(TimeSpan.FromMinutes(2))
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrphanSweeperService started. Sweep interval: {Interval}, Threshold: {Threshold}",
            SweepInterval, OrphanThreshold);

        // Wait a bit before first sweep to let the system stabilize
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOrphanedJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during orphan sweep");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }

        _logger.LogInformation("OrphanSweeperService stopped.");
    }

    private async Task SweepOrphanedJobsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Starting orphan sweep...");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

        var cutoff = DateTime.UtcNow - OrphanThreshold;

        // Find jobs stuck in non-terminal states beyond the threshold
        var orphanedTrackers = await db.JobTrackers
            .Include(t => t.Job)
            .Where(t => t.Job != null
                && StuckStatuses.Contains(t.Job.Status)
                && t.UpdatedAt < cutoff
                && t.CompletedAt == null)
            .ToListAsync(ct);

        if (orphanedTrackers.Count == 0)
        {
            _logger.LogDebug("No orphaned jobs found.");
            return;
        }

        _logger.LogWarning("Found {Count} orphaned job(s) to clean up.", orphanedTrackers.Count);

        foreach (var tracker in orphanedTrackers)
        {
            try
            {
                await CleanupOrphanedJobAsync(db, scope.ServiceProvider, tracker, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to clean up orphaned job {JobId} (Tracker {TrackerId})",
                    tracker.JobId, tracker.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Orphan sweep completed. Processed {Count} job(s).", orphanedTrackers.Count);
    }

    private async Task CleanupOrphanedJobAsync(
        FrostStreamDbContext db,
        IServiceProvider sp,
        Shared.Entities.JobTracker tracker,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "Cleaning up orphaned job {JobId}: StorageKey={StorageKey}, StoragePath={StoragePath}",
            tracker.JobId, tracker.StorageKey, tracker.StoragePath);

        // Step 1: If there's a storage path, try to delete the orphaned file
        if (!string.IsNullOrEmpty(tracker.StoragePath))
        {
            await TryDeleteOrphanedFileAsync(sp, tracker, ct);
        }

        // Step 2: Mark the job as Failed
        if (tracker.Job != null)
        {
            tracker.Job.Status = "Failed";
            tracker.Job.ErrorMsg = $"Job orphaned — stuck for over {OrphanThreshold.TotalHours} hours. Cleaned up by sweeper.";
        }

        tracker.ErrorDetails = "Marked as failed by OrphanSweeperService";
        tracker.UpdatedAt = DateTime.UtcNow;
    }

    private async Task TryDeleteOrphanedFileAsync(
        IServiceProvider sp,
        Shared.Entities.JobTracker tracker,
        CancellationToken ct)
    {
        try
        {
            // Get the storage configuration for this tracker's storage key
            var messageBus = sp.GetRequiredService<FlySwattr.NATS.Abstractions.IMessageBus>();
            var storageCfg = await messageBus.RequestAsync<StorageConfigRequest, StorageConfigResponse>(
                Shared.Subjects.StorageConfig,
                new StorageConfigRequest(tracker.StorageKey),
                TimeSpan.FromSeconds(10),
                ct);

            if (storageCfg == null || !storageCfg.Found)
            {
                _logger.LogWarning(
                    "Cannot clean up orphaned file for job {JobId}: storage config not found for key {StorageKey}",
                    tracker.JobId, tracker.StorageKey);
                return;
            }

            var storage = FluentStorageProvider.CreateStorage(storageCfg);

            // Check if the file exists (best effort)
            var exists = await _resiliencePipeline.ExecuteAsync(async token =>
            {
                var blobs = await storage.ListAsync(folderPath: null, cancellationToken: token);
                return blobs?.Any(b => b.FullPath == tracker.StoragePath) ?? false;
            }, ct);

            if (exists)
            {
                await _resiliencePipeline.ExecuteAsync(async token =>
                {
                    await storage.DeleteAsync(new[] { tracker.StoragePath! }, token);
                }, ct);

                _logger.LogInformation("Deleted orphaned file at {Path} for job {JobId}",
                    tracker.StoragePath, tracker.JobId);
            }
            else
            {
                _logger.LogDebug("No orphaned file found at {Path} for job {JobId}",
                    tracker.StoragePath, tracker.JobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete orphaned file at {Path} for job {JobId}. Continuing with status update.",
                tracker.StoragePath, tracker.JobId);
        }
    }
}
