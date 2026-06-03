using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Serves the expected-file inventory to the Worker's filesystem rescan and persists
/// the reconciliation findings it reports back. The Worker owns storage access; this
/// service owns the database, so the two cooperate over NATS request/reply.
/// </summary>
public sealed class FilesystemRescanConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IClock clock,
    ILogger<FilesystemRescanConsumerService> logger) : BackgroundService
{
    private const string QueueGroup = "databridge-filesystem-rescan";
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<FilesystemRescanInventoryRequest>(
            FilesystemRescanSubjects.Inventory, HandleInventoryAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<FilesystemRescanReportRequest>(
            FilesystemRescanSubjects.Report, HandleReportAsync, QueueGroup, stoppingToken));

        logger.LogInformation("Subscribed to filesystem rescan inventory/report subjects.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task HandleInventoryAsync(IMessageContext<FilesystemRescanInventoryRequest> context)
    {
        try
        {
            var storages = await LoadContentInventoryAsync(CancellationToken.None);
            var sidecars = await LoadSidecarPathsAsync(CancellationToken.None);

            await context.RespondAsync(new FilesystemRescanInventoryResponse
            {
                Success = true,
                Storages = storages,
                SidecarPaths = sidecars
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed building filesystem rescan inventory.");
            await context.RespondAsync(new FilesystemRescanInventoryResponse
            {
                Success = false,
                ErrorMessage = "Failed to build filesystem rescan inventory."
            });
        }
    }

    private async Task<IReadOnlyList<FilesystemStorageInventoryDto>> LoadContentInventoryAsync(CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<string, List<FilesystemContentPathDto>>(StringComparer.Ordinal);

        await using var command = dataSource.CreateCommand(
            "SELECT storage_key, storage_path, media_guid FROM media_content_id_versions;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var storageKey = reader.GetString(0);
            var path = new FilesystemContentPathDto
            {
                StoragePath = reader.GetString(1),
                MediaGuid = reader.GetGuid(2)
            };

            if (!grouped.TryGetValue(storageKey, out var list))
            {
                list = [];
                grouped[storageKey] = list;
            }

            list.Add(path);
        }

        return grouped
            .Select(kvp => new FilesystemStorageInventoryDto { StorageKey = kvp.Key, Paths = kvp.Value })
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> LoadSidecarPathsAsync(CancellationToken cancellationToken)
    {
        var paths = new List<string>();

        await using var command = dataSource.CreateCommand("""
            SELECT thumbnail_storage_path FROM metadata.media_metadata WHERE thumbnail_storage_path IS NOT NULL
            UNION
            SELECT storage_path FROM metadata.media_captions WHERE storage_path IS NOT NULL
            UNION
            SELECT avatar_storage_path FROM metadata.accounts WHERE avatar_storage_path IS NOT NULL
            UNION
            SELECT banner_storage_path FROM metadata.accounts WHERE banner_storage_path IS NOT NULL
            UNION
            SELECT info_json_storage_path FROM download_jobs WHERE info_json_storage_path IS NOT NULL;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    private async Task HandleReportAsync(IMessageContext<FilesystemRescanReportRequest> context)
    {
        var message = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            var now = clock.GetCurrentInstant();

            var existing = await db.FilesystemRescanFindings
                .Where(x => x.StorageKey == message.StorageKey)
                .ToListAsync(CancellationToken.None);

            var existingByKey = existing.ToDictionary(x => (x.StoragePath, x.FindingType));
            var reported = new HashSet<(string, FilesystemRescanFindingType)>();

            foreach (var finding in message.Findings)
            {
                reported.Add((finding.StoragePath, finding.FindingType));
                if (existingByKey.TryGetValue((finding.StoragePath, finding.FindingType), out var row))
                {
                    row.LastSeenAt = now;
                    row.ResolvedAt = null;
                    row.MediaGuid = finding.MediaGuid;
                }
                else
                {
                    db.FilesystemRescanFindings.Add(new FilesystemRescanFindingEntity
                    {
                        StorageKey = message.StorageKey,
                        StoragePath = finding.StoragePath,
                        FindingType = finding.FindingType,
                        MediaGuid = finding.MediaGuid,
                        DetectedAt = now,
                        LastSeenAt = now
                    });
                }
            }

            var resolvedCount = 0;
            foreach (var row in existing)
            {
                if (row.ResolvedAt is null && !reported.Contains((row.StoragePath, row.FindingType)))
                {
                    row.ResolvedAt = now;
                    resolvedCount++;
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);

            logger.LogInformation(
                "Filesystem rescan report for storage key {StorageKey}: {OpenCount} open finding(s), {ResolvedCount} resolved.",
                message.StorageKey,
                message.Findings.Count,
                resolvedCount);

            await context.RespondAsync(new FilesystemRescanReportResponse
            {
                Success = true,
                OpenCount = message.Findings.Count,
                ResolvedCount = resolvedCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed persisting filesystem rescan report for storage key {StorageKey}.", message.StorageKey);
            await context.RespondAsync(new FilesystemRescanReportResponse
            {
                Success = false,
                ErrorMessage = "Failed to persist filesystem rescan report."
            });
        }
    }
}
