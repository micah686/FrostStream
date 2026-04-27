using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace Worker.Services;

/// <summary>
/// Worker-side consumer for the download flow's commands. Each handler currently
/// simulates the side effect with a 1-second delay and emits the matching success
/// event. Swap each <see cref="Task.Delay(int)"/> for the real yt-dlp /
/// <c>IBlobStorageProvider</c> calls when ready.
///
/// Modelled on <c>StorageCrudConsumerService</c> in DataBridge — same queue group
/// pattern, same subscription disposal semantics.
/// </summary>
public sealed class DownloadCommandsConsumerService(
    IMessageBus messageBus,
    IClock clock,
    ILogger<DownloadCommandsConsumerService> logger) : BackgroundService
{
    private const string QueueGroup = "workers";
    private static readonly TimeSpan StubLatency = TimeSpan.FromSeconds(1);

    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<FetchMetadataCommand>(
            DownloadSubjects.FetchMetadataCommand,
            HandleFetchMetadataAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<DownloadVideoCommand>(
            DownloadSubjects.DownloadVideoCommand,
            HandleDownloadVideoAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<UploadObjectCommand>(
            DownloadSubjects.UploadObjectCommand,
            HandleUploadObjectAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<DeleteTempFileCommand>(
            DownloadSubjects.DeleteTempFileCommand,
            HandleDeleteTempFileAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<DeleteUploadedObjectCommand>(
            DownloadSubjects.DeleteUploadedObjectCommand,
            HandleDeleteUploadedObjectAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        logger.LogInformation("Subscribed to {Count} download command subjects.", _subscriptions.Count);

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

    private async Task HandleFetchMetadataAsync(IMessageContext<FetchMetadataCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: invoke yt-dlp via YoutubeDLSharp to extract source metadata
            //       (title, uploader, provider id, raw json).
            await Task.Delay(StubLatency);

            await messageBus.PublishAsync(DownloadSubjects.MetadataFetched, new MetadataFetched
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                ArchiveKey = $"stub:{cmd.JobId:N}",
                Provider = "stub",
                SourceVideoId = cmd.JobId.ToString("N"),
                Title = "Stub Title",
                Uploader = "Stub Uploader",
                RawMetadataJson = null
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FetchMetadata stub failed for JobId {JobId}", cmd.JobId);
            await PublishMetadataFailedAsync(cmd, ex);
        }
    }

    private async Task HandleDownloadVideoAsync(IMessageContext<DownloadVideoCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: run yt-dlp against cmd.SourceUrl, write to a worker-local temp directory,
            //       and compute XxHash128 over the resulting file.
            await Task.Delay(StubLatency);

            await messageBus.PublishAsync(DownloadSubjects.DownloadCompleted, new DownloadCompleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = $"/tmp/froststream/{cmd.JobId:N}/video.bin",
                FileName = "video.bin",
                FileSizeBytes = 0,
                ContentHashXxh128 = null,
                ContentType = "application/octet-stream"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DownloadVideo stub failed for JobId {JobId}", cmd.JobId);
            await PublishDownloadFailedAsync(cmd, ex);
        }
    }

    private async Task HandleUploadObjectAsync(IMessageContext<UploadObjectCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: resolve cmd.StorageKey via IBlobStorageProvider and upload cmd.TempFileRef
            //       into the resulting backend; capture the final ObjectKey/StorageVersion.
            await Task.Delay(StubLatency);

            await messageBus.PublishAsync(DownloadSubjects.UploadCompleted, new UploadCompleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = cmd.TempFileRef,
                StorageKey = cmd.StorageKey,
                ObjectKey = $"archives/{cmd.ArchiveKey}/video.bin",
                StorageVersion = null,
                ContentHashXxh128 = null,
                ContentLengthBytes = null
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UploadObject stub failed for JobId {JobId}", cmd.JobId);
            await PublishUploadFailedAsync(cmd, ex);
        }
    }

    private async Task HandleDeleteTempFileAsync(IMessageContext<DeleteTempFileCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: delete cmd.TempFileRef from worker-local storage.
            await Task.Delay(StubLatency);

            await messageBus.PublishAsync(DownloadSubjects.TempFileDeleted, new TempFileDeleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = cmd.TempFileRef
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteTempFile stub failed for JobId {JobId}", cmd.JobId);
            await messageBus.PublishAsync(DownloadSubjects.TempFileDeleteFailed, new TempFileDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = cmd.TempFileRef,
                FailureKind = FailureKind.Transient,
                ErrorMessage = ex.Message
            });
        }
    }

    private async Task HandleDeleteUploadedObjectAsync(IMessageContext<DeleteUploadedObjectCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: delete (cmd.StorageKey, cmd.ObjectKey, cmd.StorageVersion) from final storage
            //       via IBlobStorageProvider.
            await Task.Delay(StubLatency);

            await messageBus.PublishAsync(DownloadSubjects.UploadedObjectDeleted, new UploadedObjectDeleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                StorageKey = cmd.StorageKey,
                ObjectKey = cmd.ObjectKey,
                StorageVersion = cmd.StorageVersion
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteUploadedObject stub failed for JobId {JobId}", cmd.JobId);
            await messageBus.PublishAsync(DownloadSubjects.UploadedObjectDeleteFailed, new UploadedObjectDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                StorageKey = cmd.StorageKey,
                ObjectKey = cmd.ObjectKey,
                StorageVersion = cmd.StorageVersion,
                FailureKind = FailureKind.Transient,
                ErrorMessage = ex.Message
            });
        }
    }

    private Task PublishMetadataFailedAsync(FetchMetadataCommand cmd, Exception ex)
        => messageBus.PublishAsync(DownloadSubjects.MetadataFetchFailed, new MetadataFetchFailed
        {
            JobId = cmd.JobId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = Guid.NewGuid(),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = FailureKind.Transient,
            ErrorMessage = ex.Message
        });

    private Task PublishDownloadFailedAsync(DownloadVideoCommand cmd, Exception ex)
        => messageBus.PublishAsync(DownloadSubjects.DownloadFailed, new DownloadFailed
        {
            JobId = cmd.JobId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = Guid.NewGuid(),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = FailureKind.Transient,
            ErrorMessage = ex.Message
        });

    private Task PublishUploadFailedAsync(UploadObjectCommand cmd, Exception ex)
        => messageBus.PublishAsync(DownloadSubjects.UploadFailed, new UploadFailed
        {
            JobId = cmd.JobId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = Guid.NewGuid(),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = FailureKind.Transient,
            ErrorMessage = ex.Message,
            TempFileRef = cmd.TempFileRef
        });
}
