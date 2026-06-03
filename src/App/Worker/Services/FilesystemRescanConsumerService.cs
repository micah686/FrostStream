using System.IO.Pipelines;
using System.Text.Json;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;
using Shared.Storage;

namespace Worker.Services;

/// <summary>
/// Reconciles the configured storage filesystems against the database. The Worker is the
/// only service with blob-storage access, so it uploads each actual storage listing to the
/// NATS object store and asks DataBridge to reconcile that listing inside Postgres.
/// </summary>
public sealed class FilesystemRescanConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    Func<string, IObjectStore> objectStoreFactory,
    IStorageEnumerator storageEnumerator,
    IClock clock,
    ILogger<FilesystemRescanConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);
    private static readonly TimeSpan StorageKeysTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconcileTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Subscribing to filesystem rescan consumer on stream {Stream}.", Stream.Value);

        await consumer.ConsumePullAsync<FilesystemRescanRequested>(
            Stream,
            ConsumerName.From(BackgroundJobsTopology.WorkerFilesystemRescanConsumer),
            async context =>
            {
                var message = context.Message;
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeatTask = RunHeartbeatAsync(context, heartbeatCts.Token);
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
                finally
                {
                    await heartbeatCts.CancelAsync();
                    await heartbeatTask;
                }
            },
            options: null,
            cancellationToken: stoppingToken);
    }

    private async Task HandleAsync(FilesystemRescanRequested request, CancellationToken cancellationToken)
    {
        await MarkAttemptAsync(request.ScheduleKey);

        var storageKeys = await messageBus.RequestAsync<FilesystemRescanStorageKeysRequest, FilesystemRescanStorageKeysResponse>(
            FilesystemRescanSubjects.StorageKeys,
            new FilesystemRescanStorageKeysRequest(),
            StorageKeysTimeout,
            cancellationToken);

        if (storageKeys is not { Success: true })
        {
            throw new InvalidOperationException(storageKeys?.ErrorMessage ?? "Filesystem rescan storage-key request failed.");
        }

        var failed = false;
        foreach (var storageKey in storageKeys.StorageKeys)
        {
            try
            {
                await ReconcileStorageAsync(request.ScheduleKey, storageKey, cancellationToken);
            }
            catch (Exception ex)
            {
                failed = true;
                logger.LogError(ex, "Filesystem rescan failed for storage key {StorageKey}.", storageKey);
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
            storageKeys.StorageKeys.Count);
    }

    private async Task ReconcileStorageAsync(
        string scheduleKey,
        string storageKey,
        CancellationToken cancellationToken)
    {
        var objectStore = objectStoreFactory(BackgroundJobsTopology.FilesystemRescanObjectStoreBucket);
        var objectKey = BuildObjectKey(scheduleKey, storageKey, clock.GetCurrentInstant());
        var pipe = new Pipe();

        logger.LogInformation(
            "Uploading filesystem listing for storage key {StorageKey} to object {ObjectKey}.",
            storageKey,
            objectKey);

        var putTask = objectStore.PutAsync(objectKey, pipe.Reader.AsStream(), cancellationToken);
        try
        {
            Exception? writeException = null;
            try
            {
                await WriteStorageListingAsync(storageKey, pipe.Writer.AsStream(leaveOpen: true), cancellationToken);
            }
            catch (Exception ex)
            {
                writeException = ex;
                throw;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(writeException);
            }

            await putTask;
        }
        catch
        {
            try
            {
                await putTask;
            }
            catch
            {
                // The original write/upload failure is logged by the caller.
            }

            throw;
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
        }

        var response = await messageBus.RequestAsync<FilesystemRescanReconcileRequest, FilesystemRescanReconcileResponse>(
            FilesystemRescanSubjects.Reconcile,
            new FilesystemRescanReconcileRequest
            {
                ScheduleKey = scheduleKey,
                StorageKey = storageKey,
                ObjectBucket = BackgroundJobsTopology.FilesystemRescanObjectStoreBucket,
                ObjectKey = objectKey
            },
            ReconcileTimeout,
            cancellationToken);

        if (response is not { Success: true })
        {
            throw new InvalidOperationException(response?.ErrorMessage ?? "Filesystem rescan reconcile request failed.");
        }

        logger.LogInformation(
            "Reconciled storage key {StorageKey}: {Missing} missing, {Unexpected} unexpected file(s).",
            storageKey,
            response.MissingCount,
            response.UnexpectedCount);
    }

    private async Task WriteStorageListingAsync(
        string storageKey,
        Stream target,
        CancellationToken cancellationToken)
    {
        var count = 0;
        await using var writer = new StreamWriter(target, leaveOpen: true);
        await foreach (var path in storageEnumerator.EnumerateFilePathsAsync(storageKey, cancellationToken))
        {
            var normalized = StoragePathNormalizer.Normalize(path);
            await writer.WriteLineAsync(JsonSerializer.Serialize(normalized).AsMemory(), cancellationToken);
            count++;
        }

        await writer.FlushAsync(cancellationToken);

        logger.LogInformation(
            "Uploaded {Count} normalized file path(s) for filesystem rescan storage key {StorageKey}.",
            count,
            storageKey);
    }

    private static string BuildObjectKey(string scheduleKey, string storageKey, Instant now)
        => string.Join(
            '/',
            EscapeKeyPart(scheduleKey),
            EscapeKeyPart(storageKey),
            $"{now.ToDateTimeOffset():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.ndjson");

    private static string EscapeKeyPart(string value)
        => Uri.EscapeDataString(value).Replace("%", "_", StringComparison.Ordinal);

    private async Task RunHeartbeatAsync<T>(IJsMessageContext<T> context, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await context.InProgressAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Filesystem rescan in-progress heartbeat failed.");
        }
    }

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
