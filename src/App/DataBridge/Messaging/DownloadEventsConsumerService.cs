using System.Text.Json;
using DataBridge;
using DataBridge.Data;
using DataBridge.Flows;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Consumes Worker-emitted result events for the download flow from JetStream.
/// For each event:
///   1. Dedupe via <c>processed_messages</c>.
///   2. Persist the business-visible state change (<c>download_jobs</c>) and append
///      to <c>download_job_history</c>.
///   3. Forward the event to the Cleipnir flow via the per-instance message writer with
///      <see cref="IFlowMessage.OperationKey"/> as Cleipnir's idempotency key — so even if
///      ingress dedupe is bypassed (e.g. a different MessageId for the same logical event),
///      Cleipnir's message store will deduplicate by OperationKey before the flow advances.
///   4. Mark the message processed in the same transaction as the state/history write.
///   5. Ack JetStream — only after every step above has succeeded.
///
/// All work runs under a single fresh DI scope per message so the scoped
/// <see cref="DataBridgeDbContext"/> isn't shared across messages. One
/// <see cref="IJetStreamConsumer.ConsumePullAsync"/> task per result subject runs in
/// parallel; <see cref="ExecuteAsync"/> awaits all of them with <see cref="Task.WhenAll(Task[])"/>.
/// </summary>
public sealed class DownloadEventsConsumerService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    DownloadArchiveFlows flows,
    ILogger<DownloadEventsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(DownloadTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            Consume<MetadataFetched>(DownloadTopology.MetadataFetchedConsumer, ApplyMetadataFetchedAsync, stoppingToken),
            Consume<MetadataFetchFailed>(DownloadTopology.MetadataFetchFailedConsumer, NoStateChange, stoppingToken),
            Consume<DownloadCompleted>(DownloadTopology.DownloadCompletedConsumer, ApplyDownloadCompletedAsync, stoppingToken),
            Consume<DownloadFailed>(DownloadTopology.DownloadFailedConsumer, NoStateChange, stoppingToken),
            Consume<UploadCompleted>(DownloadTopology.UploadCompletedConsumer, ApplyUploadCompletedAsync, stoppingToken),
            Consume<UploadFailed>(DownloadTopology.UploadFailedConsumer, NoStateChange, stoppingToken),
            Consume<TempFileDeleted>(DownloadTopology.TempFileDeletedConsumer, NoStateChange, stoppingToken),
            Consume<TempFileDeleteFailed>(DownloadTopology.TempFileDeleteFailedConsumer, NoStateChange, stoppingToken),
            Consume<UploadedObjectDeleted>(DownloadTopology.UploadedObjectDeletedConsumer, NoStateChange, stoppingToken),
            Consume<UploadedObjectDeleteFailed>(DownloadTopology.UploadedObjectDeleteFailedConsumer, NoStateChange, stoppingToken),
        };

        logger.LogInformation("Subscribed to {Count} download event consumers on stream {Stream}.", consumers.Length, Stream.Value);
        return Task.WhenAll(consumers);
    }

    private Task Consume<TEvent>(
        string consumerName,
        Func<IDownloadJobsRepository, TEvent, CancellationToken, Task> persist,
        CancellationToken stoppingToken)
        where TEvent : class, IFlowMessage
        => consumer.ConsumePullAsync<TEvent>(
            stream: Stream,
            consumer: ConsumerName.From(consumerName),
            handler: ctx => HandleAsync(ctx, persist),
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync<TEvent>(
        IJsMessageContext<TEvent> context,
        Func<IDownloadJobsRepository, TEvent, CancellationToken, Task> persist)
        where TEvent : class, IFlowMessage
    {
        var evt = context.Message;
        var eventName = typeof(TEvent).Name;

        try
        {
            await scopeFactory.WithScopedAsync<IDownloadJobsRepository, DataBridgeDbContext>(async (jobs, db) =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                if (!await jobs.TryMarkMessageProcessedAsync(evt.MessageId, evt.OperationKey, evt.JobId))
                {
                    await tx.CommitAsync();
                    await context.AckAsync();
                    return;
                }

                await persist(jobs, evt, CancellationToken.None);

                await jobs.RecordHistoryAsync(
                    evt.JobId,
                    evt.MessageId,
                    evt.OperationKey,
                    eventName,
                    JsonSerializer.Serialize<TEvent>(evt));

                // Keep the flow handoff before committing processed_messages: if the process
                // dies after commit, redelivery will be acked as processed and will not resend.
                // If commit fails after this send, redelivery may resend and Cleipnir's
                // OperationKey idempotency absorbs the duplicate.
                await flows.SendMessage(evt.JobId.ToString("N"), evt, idempotencyKey: evt.OperationKey);

                await tx.CommitAsync();
                await context.AckAsync();
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling {EventName} for JobId {JobId}; nacking for redelivery", eventName, evt.JobId);
            await context.NackAsync();
        }
    }

    private static Task ApplyMetadataFetchedAsync(IDownloadJobsRepository jobs, MetadataFetched evt, CancellationToken ct)
        => jobs.ApplyMetadataAsync(evt.JobId, evt, ct);

    private static Task ApplyDownloadCompletedAsync(IDownloadJobsRepository jobs, DownloadCompleted evt, CancellationToken ct)
        => jobs.ApplyDownloadCompletedAsync(evt.JobId, evt, ct);

    private static Task ApplyUploadCompletedAsync(IDownloadJobsRepository jobs, UploadCompleted evt, CancellationToken ct)
        => evt.Kind switch
        {
            UploadArtifactKind.InfoJson => jobs.ApplySidecarUploadCompletedAsync(evt.JobId, evt, ct),
            _ => jobs.CommitUploadAsync(evt.JobId, evt, ct)
        };

    private static Task NoStateChange<TEvent>(IDownloadJobsRepository jobs, TEvent evt, CancellationToken ct)
        where TEvent : IFlowMessage
        => Task.CompletedTask;
}
