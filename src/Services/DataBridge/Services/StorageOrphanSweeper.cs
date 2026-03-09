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
/// Background service that periodically scans storage for orphaned files
/// (files without corresponding database records) and moves them to a quarantine area.
/// Complements the DbOrphanSweeper which handles jobs stuck in the database.
/// </summary>
public class StorageOrphanSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StorageOrphanSweeper> _logger;

    /// <summary>How often the sweeper runs.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly ResiliencePipeline _resiliencePipeline;

    public StorageOrphanSweeper(
        IServiceScopeFactory scopeFactory,
        ILogger<StorageOrphanSweeper> logger)
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
            .AddTimeout(TimeSpan.FromMinutes(5))
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageOrphanSweeper started. Sweep interval: {Interval}", SweepInterval);

        // Wait a bit before first sweep to let the system stabilize
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepStorageOrphansAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during storage orphan sweep");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }

        _logger.LogInformation("StorageOrphanSweeper stopped.");
    }

    private async Task SweepStorageOrphansAsync(CancellationToken ct)
    {
        _logger.LogDebug("Starting storage orphan sweep...");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

        // Get all storage paths from DB
        var dbPaths = await db.VideoVersions
            .AsNoTracking()
            .Select(v => v.StoragePath)
            .Where(p => p != null)
            .ToHashSetAsync(ct);

        _logger.LogDebug("Found {Count} video versions in database", dbPaths.Count);

        // Get storage configuration
        var storage = await GetStorageAsync(scope.ServiceProvider, ct);
        if (storage == null)
        {
            _logger.LogWarning("Could not get storage for orphan sweep");
            return;
        }

        // List all objects in storage (use ListOptions for recursive listing)
        var storageObjects = await _resiliencePipeline.ExecuteAsync(async token =>
        {
            return await storage.ListAsync(new ListOptions { Recurse = true }, cancellationToken: token);
        }, ct);

        var orphansFound = 0;
        foreach (var obj in storageObjects)
        {
            // Skip already quarantined files
            if (obj.FullPath.StartsWith("_orphans/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!dbPaths.Contains(obj.FullPath))
            {
                orphansFound++;
                await HandleOrphanedObjectAsync(storage, obj, ct);
            }
        }

        if (orphansFound == 0)
        {
            _logger.LogDebug("No storage orphans found.");
        }
        else
        {
            _logger.LogWarning("Storage orphan sweep completed. Found and quarantined {Count} orphaned object(s).", orphansFound);
        }
    }

    private async Task HandleOrphanedObjectAsync(IBlobStorage storage, Blob storageObject, CancellationToken ct)
    {
        _logger.LogWarning(
            "Found orphaned storage object: {Path}. Size: {Size:N0} bytes",
            storageObject.FullPath,
            storageObject.Size);

        try
        {
            // Move to quarantine for review rather than deleting immediately
            var quarantinePath = $"_orphans/{storageObject.FullPath}";

            // Open the source file for reading
            Stream? sourceStream = null;
            await _resiliencePipeline.ExecuteAsync(async token =>
            {
                sourceStream = await storage.OpenReadAsync(storageObject.FullPath, cancellationToken: token);
            }, ct);

            if (sourceStream == null)
            {
                _logger.LogWarning("Could not open orphaned object for reading: {Path}", storageObject.FullPath);
                return;
            }

            // Copy to quarantine location
            await using (sourceStream)
            {
                await _resiliencePipeline.ExecuteAsync(async token =>
                {
                    await storage.WriteAsync(quarantinePath, sourceStream, cancellationToken: token);
                }, ct);
            }

            // Delete the original
            await _resiliencePipeline.ExecuteAsync(async token =>
            {
                await storage.DeleteAsync(new[] { storageObject.FullPath }, token);
            }, ct);

            _logger.LogInformation(
                "Moved orphaned object to quarantine: {OriginalPath} -> {QuarantinePath}",
                storageObject.FullPath,
                quarantinePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to quarantine orphaned object: {Path}. Manual cleanup may be required.",
                storageObject.FullPath);
        }
    }

    private async Task<IBlobStorage?> GetStorageAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            var messageBus = sp.GetRequiredService<FlySwattr.NATS.Abstractions.IMessageBus>();

            // Try to get default storage config
            var storageCfg = await _resiliencePipeline.ExecuteAsync(async token =>
            {
                return await messageBus.RequestAsync<StorageConfigRequest, StorageConfigResponse>(
                    Shared.Subjects.StorageConfig,
                    new StorageConfigRequest("default"),
                    TimeSpan.FromSeconds(10),
                    token);
            }, ct);

            if (storageCfg == null || !storageCfg.Found)
            {
                _logger.LogWarning("Default storage config not found");
                return null;
            }

            return FluentStorageProvider.CreateStorage(storageCfg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get storage for orphan sweep");
            return null;
        }
    }
}
