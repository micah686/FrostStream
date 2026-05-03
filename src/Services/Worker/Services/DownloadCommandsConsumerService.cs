using System.Text.Json;
using System.Globalization;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Media;
using Shared.Messaging;
using YtDlpSharpLib;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Models;

namespace Worker.Services;

/// <summary>
/// Worker-side JetStream consumer for the download flow's commands. Each handler currently
/// simulates the side effect with a 1-second delay and emits the matching success event.
/// Swap each <see cref="Task.Delay(int)"/> for the real yt-dlp / <c>IBlobStorageProvider</c>
/// calls when ready.
///
/// Consumer durables and the FROSTSTREAM_DOWNLOAD stream are provisioned by
/// <see cref="DownloadTopology"/>; both DataBridge and Worker register it, so whichever
/// service starts first creates them.
///
/// === DEDUPE CAVEAT — READ BEFORE REPLACING THE STUBS ===
/// Every result-event constructor below uses <c>MessageId = Guid.NewGuid()</c>. That is OK
/// for the stub (the side effect is a no-op delay), but it is NOT safe for the real
/// implementation because JetStream is at-least-once:
///
///   1. Worker pulls command C, runs the side effect (e.g. yt-dlp succeeds).
///   2. Worker publishes the result event with a fresh Guid, then crashes/times-out
///      before <c>AckAsync()</c>.
///   3. JetStream redelivers C; Worker runs the side effect again (yt-dlp re-downloads,
///      or storage uploads twice), and publishes a SECOND result event with another
///      fresh Guid.
///
/// Today, DataBridge's <c>processed_messages</c> dedupe filters by <c>MessageId</c> — but
/// because each redelivery generates a new Guid, both events pass that check and both reach
/// Cleipnir. Cleipnir's secondary dedupe by <c>OperationKey</c> catches it (we use
/// <c>$"{cmd.OperationKey}/result"</c> which is stable across redeliveries), so the flow
/// itself doesn't double-advance — but the side effect ran twice and storage may have
/// double-billed bytes.
///
/// === HOW TO FIX WHEN YOU REPLACE THE STUBS ===
/// Derive the result event's <c>MessageId</c> deterministically from the command so a
/// redelivered command produces the same event identity:
///
///   var resultMessageId = DeterministicGuid.Create(cmd.MessageId, "/result");
///
/// (Any stable hash → Guid will do — e.g. SHA-256 of <c>cmd.MessageId.ToString("N") + "/result"</c>
/// folded to 16 bytes, or <c>System.IO.Hashing.XxHash128</c> over the same input since the
/// project already pulls in <c>System.IO.Hashing</c>.) Then:
///
///   • The JetStream <c>Nats-Msg-Id</c> header set by <see cref="Publish{T}"/> becomes stable,
///     so JetStream's own 2-minute dedupe window suppresses the duplicate publish on the
///     server side.
///   • DataBridge's <c>processed_messages</c> row gets the same MessageId on the second
///     delivery and dedupes correctly.
///   • Cleipnir's <c>OperationKey</c> dedupe stays as the third line of defense.
///
/// Also: in the real impl, run the side effect inside a per-command idempotency guard
/// (Worker-local KV or a local SQLite ledger) so yt-dlp / storage upload don't actually
/// re-execute on redelivery — only the result publish + ack should re-run.
/// </summary>
public sealed class DownloadCommandsConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    IYtDlpClient ytDlp,
    IClock clock,
    ILogger<DownloadCommandsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(DownloadTopology.StreamNameValue);
    private static readonly TimeSpan StubLatency = TimeSpan.FromSeconds(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            Consume<FetchMetadataCommand>(DownloadTopology.WorkerFetchMetadataConsumer, HandleFetchMetadataAsync, stoppingToken),
            Consume<DownloadVideoCommand>(DownloadTopology.WorkerDownloadVideoConsumer, HandleDownloadVideoAsync, stoppingToken),
            Consume<UploadObjectCommand>(DownloadTopology.WorkerUploadObjectConsumer, HandleUploadObjectAsync, stoppingToken),
            Consume<DeleteTempFileCommand>(DownloadTopology.WorkerDeleteTempFileConsumer, HandleDeleteTempFileAsync, stoppingToken),
            Consume<DeleteUploadedObjectCommand>(DownloadTopology.WorkerDeleteUploadedObjectConsumer, HandleDeleteUploadedObjectAsync, stoppingToken),
        };

        logger.LogInformation("Subscribed to {Count} download command consumers on stream {Stream}.", consumers.Length, Stream.Value);
        return Task.WhenAll(consumers);
    }

    private Task Consume<TCommand>(
        string consumerName,
        Func<IJsMessageContext<TCommand>, Task> handler,
        CancellationToken stoppingToken)
        where TCommand : class, IFlowMessage
        => consumer.ConsumePullAsync<TCommand>(
            stream: Stream,
            consumer: ConsumerName.From(consumerName),
            handler: handler,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleFetchMetadataAsync(IJsMessageContext<FetchMetadataCommand> context)
    {
        var cmd = context.Message;

        try
        {
            var metadataResult = await ytDlp.TryGetVideoInfoAsync(cmd.SourceUrl);
            if (!metadataResult.Success || metadataResult.Data is not { } info)
            {
                throw new YtDlpProcessException(
                    $"yt-dlp metadata fetch failed for {cmd.SourceUrl}",
                    command: null,
                    exitCode: null,
                    lastStderrLines: metadataResult.ErrorOutput);
            }

            var provider = !string.IsNullOrWhiteSpace(info.Extractor)
                ? info.Extractor
                : info.ExtractorKey;
            var sourceMediaId = info.Id ?? info.DisplayId;
            var sourceLastModified = ResolveSourceLastModified(info);
            var sourceMetadataHash = MediaSourceIdentity.TryCreateSourceMetadataHash(
                provider,
                sourceMediaId,
                sourceLastModified);
            var archiveKey = !string.IsNullOrWhiteSpace(sourceMetadataHash)
                ? $"source/{sourceMetadataHash}"
                : cmd.JobId.ToString("N");

            await Publish(DownloadSubjects.MetadataFetched, new MetadataFetched
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                ArchiveKey = archiveKey,
                Provider = provider,
                SourceMediaId = sourceMediaId,
                SourceLastModified = sourceLastModified,
                SourceMetadataHash = sourceMetadataHash,
                Title = info.Title ?? info.FullTitle,
                Uploader = info.Uploader ?? info.Channel,
            });
            await context.AckAsync();
        }
        catch (YtDlpUnavailableException ex)
        {
            logger.LogWarning(ex,
                "FetchMetadata: source unavailable for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishMetadataFailedAsync(cmd, ex, FailureKind.Permanent);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "FetchMetadata failed for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishMetadataFailedAsync(cmd, ex, FailureKind.Transient);
            // Failure event published successfully → ack so we don't redeliver the command.
            await context.AckAsync();
        }
    }

    private async Task HandleDownloadVideoAsync(IJsMessageContext<DownloadVideoCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: run yt-dlp against cmd.SourceUrl, write to a worker-local temp directory,
            //       and compute XxHash128 over the resulting file.
            await Task.Delay(StubLatency);

            await Publish(DownloadSubjects.DownloadCompleted, new DownloadCompleted
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
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DownloadVideo stub failed for JobId {JobId}", cmd.JobId);
            await PublishDownloadFailedAsync(cmd, ex);
            await context.AckAsync();
        }
    }

    private async Task HandleUploadObjectAsync(IJsMessageContext<UploadObjectCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: resolve cmd.StorageKey via IBlobStorageProvider and upload cmd.TempFileRef
            //       into the resulting backend; capture the final ObjectKey/StorageVersion.
            await Task.Delay(StubLatency);

            await Publish(DownloadSubjects.UploadCompleted, new UploadCompleted
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
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UploadObject stub failed for JobId {JobId}", cmd.JobId);
            await PublishUploadFailedAsync(cmd, ex);
            await context.AckAsync();
        }
    }

    private async Task HandleDeleteTempFileAsync(IJsMessageContext<DeleteTempFileCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: delete cmd.TempFileRef from worker-local storage.
            await Task.Delay(StubLatency);

            await Publish(DownloadSubjects.TempFileDeleted, new TempFileDeleted
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
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteTempFile stub failed for JobId {JobId}", cmd.JobId);
            await Publish(DownloadSubjects.TempFileDeleteFailed, new TempFileDeleteFailed
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
            await context.AckAsync();
        }
    }

    private async Task HandleDeleteUploadedObjectAsync(IJsMessageContext<DeleteUploadedObjectCommand> context)
    {
        var cmd = context.Message;

        try
        {
            // TODO: delete (cmd.StorageKey, cmd.ObjectKey, cmd.StorageVersion) from final storage
            //       via IBlobStorageProvider.
            await Task.Delay(StubLatency);

            await Publish(DownloadSubjects.UploadedObjectDeleted, new UploadedObjectDeleted
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
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteUploadedObject stub failed for JobId {JobId}", cmd.JobId);
            await Publish(DownloadSubjects.UploadedObjectDeleteFailed, new UploadedObjectDeleteFailed
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
            await context.AckAsync();
        }
    }

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => publisher.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private static Instant? ResolveSourceLastModified(VideoInfo info)
    {
        if (info.ModifiedTimestamp is { } modifiedTimestamp)
            return Instant.FromUnixTimeSeconds(modifiedTimestamp);

        if (string.IsNullOrWhiteSpace(info.ModifiedDate))
            return null;

        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd" };
        return DateOnly.TryParseExact(
            info.ModifiedDate,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var modifiedDate)
            ? Instant.FromDateTimeOffset(new DateTimeOffset(modifiedDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            : null;
    }

    private Task PublishMetadataFailedAsync(FetchMetadataCommand cmd, Exception ex, FailureKind failureKind)
        => Publish(DownloadSubjects.MetadataFetchFailed, new MetadataFetchFailed
        {
            JobId = cmd.JobId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = failureKind,
            ErrorMessage = ex.Message
        });

    private Task PublishDownloadFailedAsync(DownloadVideoCommand cmd, Exception ex)
        => Publish(DownloadSubjects.DownloadFailed, new DownloadFailed
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
        => Publish(DownloadSubjects.UploadFailed, new UploadFailed
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
