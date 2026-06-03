using FluentStorage;
using FluentStorage.Blobs;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;

namespace Worker.Services;

/// <summary>
/// Reconciles the configured storage filesystems against the database. The Worker is the
/// only service with blob-storage access, so it lists each storage, asks DataBridge for the
/// expected inventory, and reports the differences (files missing from storage, and files
/// present in storage that the database does not track) back to DataBridge for persistence.
/// </summary>
public sealed class FilesystemRescanConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IBlobStorageProvider blobStorageProvider,
    IClock clock,
    ILogger<FilesystemRescanConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);
    private static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Subscribing to filesystem rescan consumer on stream {Stream}.", Stream.Value);

        await consumer.ConsumePullAsync<FilesystemRescanRequested>(
            Stream,
            ConsumerName.From(BackgroundJobsTopology.WorkerFilesystemRescanConsumer),
            async context =>
            {
                var message = context.Message;
                try
                {
                    await HandleAsync(message, stoppingToken);
                    await context.AckAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed handling filesystem rescan {IdempotencyKey}; nacking", message.IdempotencyKey);
                    await MarkFailureAsync(message.ScheduleKey);
                    await context.NackAsync();
                }
            },
            options: null,
            cancellationToken: stoppingToken);
    }

    private async Task HandleAsync(FilesystemRescanRequested request, CancellationToken cancellationToken)
    {
        await MarkAttemptAsync(request.ScheduleKey);

        var inventory = await messageBus.RequestAsync<FilesystemRescanInventoryRequest, FilesystemRescanInventoryResponse>(
            FilesystemRescanSubjects.Inventory,
            new FilesystemRescanInventoryRequest(),
            InventoryTimeout,
            cancellationToken);

        if (inventory is not { Success: true })
        {
            throw new InvalidOperationException(inventory?.ErrorMessage ?? "Filesystem rescan inventory request failed.");
        }

        var sidecarPaths = inventory.SidecarPaths
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

        var failed = false;
        foreach (var storage in inventory.Storages)
        {
            try
            {
                await ReconcileStorageAsync(request.ScheduleKey, storage, sidecarPaths, cancellationToken);
            }
            catch (Exception ex)
            {
                failed = true;
                logger.LogError(ex, "Filesystem rescan failed for storage key {StorageKey}.", storage.StorageKey);
            }
        }

        if (failed)
        {
            throw new InvalidOperationException("One or more storage keys failed to reconcile during filesystem rescan.");
        }

        await MarkSuccessAsync(request.ScheduleKey);
        logger.LogInformation(
            "Completed filesystem rescan {IdempotencyKey} across {Count} storage key(s).",
            request.IdempotencyKey,
            inventory.Storages.Count);
    }

    private async Task ReconcileStorageAsync(
        string scheduleKey,
        FilesystemStorageInventoryDto storage,
        IReadOnlySet<string> sidecarPaths,
        CancellationToken cancellationToken)
    {
        var blobStorage = await blobStorageProvider.GetAsync(storage.StorageKey, cancellationToken);

        var blobs = await blobStorage.ListAsync(
            new ListOptions { Recurse = true },
            cancellationToken);

        var actualPaths = blobs
            .Where(b => b.IsFile)
            .Select(b => Normalize(b.FullPath))
            .ToHashSet(StringComparer.Ordinal);

        // Expected content files for this storage key, indexed by normalized path so we can
        // both detect missing files and recognise known files during the unexpected-file sweep.
        var expectedByNormalized = new Dictionary<string, FilesystemContentPathDto>(StringComparer.Ordinal);
        foreach (var path in storage.Paths)
        {
            expectedByNormalized[Normalize(path.StoragePath)] = path;
        }

        var findings = new List<FilesystemRescanFindingDto>();

        // Files the database expects but storage no longer has.
        foreach (var (normalized, expected) in expectedByNormalized)
        {
            if (!actualPaths.Contains(normalized))
            {
                findings.Add(new FilesystemRescanFindingDto
                {
                    StoragePath = expected.StoragePath,
                    FindingType = FilesystemRescanFindingType.MissingFile,
                    MediaGuid = expected.MediaGuid
                });
            }
        }

        // Files present in storage that the database does not track (added outside the workflow).
        foreach (var blob in blobs.Where(b => b.IsFile))
        {
            var normalized = Normalize(blob.FullPath);
            if (!expectedByNormalized.ContainsKey(normalized) && !sidecarPaths.Contains(normalized))
            {
                findings.Add(new FilesystemRescanFindingDto
                {
                    StoragePath = blob.FullPath,
                    FindingType = FilesystemRescanFindingType.UnexpectedFile
                });
            }
        }

        var response = await messageBus.RequestAsync<FilesystemRescanReportRequest, FilesystemRescanReportResponse>(
            FilesystemRescanSubjects.Report,
            new FilesystemRescanReportRequest
            {
                ScheduleKey = scheduleKey,
                StorageKey = storage.StorageKey,
                Findings = findings
            },
            ReportTimeout,
            cancellationToken);

        if (response is not { Success: true })
        {
            throw new InvalidOperationException(response?.ErrorMessage ?? "Filesystem rescan report request failed.");
        }

        logger.LogInformation(
            "Reconciled storage key {StorageKey}: {Missing} missing, {Unexpected} unexpected file(s).",
            storage.StorageKey,
            findings.Count(f => f.FindingType == FilesystemRescanFindingType.MissingFile),
            findings.Count(f => f.FindingType == FilesystemRescanFindingType.UnexpectedFile));
    }

    private static string Normalize(string path) => StoragePath.Normalize(path);

    private Task MarkAttemptAsync(string scheduleKey)
        => messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
        {
            Key = scheduleKey,
            AttemptedAt = clock.GetCurrentInstant()
        });

    private Task MarkSuccessAsync(string scheduleKey)
        => messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
        {
            Key = scheduleKey,
            SucceededAt = clock.GetCurrentInstant()
        });

    private Task MarkFailureAsync(string scheduleKey)
        => messageBus.PublishAsync(ScheduleSubjects.MarkFailure, new ScheduleMarkFailureRequestMessage
        {
            Key = scheduleKey,
            FailedAt = clock.GetCurrentInstant()
        });
}
