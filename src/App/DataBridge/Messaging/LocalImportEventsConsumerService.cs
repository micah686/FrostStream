using DataBridge.Flows;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class LocalImportEventsConsumerService(
    IJetStreamConsumer consumer,
    LocalMediaImportFlows flows,
    ILogger<LocalImportEventsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName ImportStream = StreamName.From(LocalImportTopology.StreamNameValue);
    private static readonly StreamName DownloadStream = StreamName.From(DownloadTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            ConsumeImport<LocalImportFilePrepared>(LocalImportTopology.LocalImportFilePreparedConsumer, stoppingToken),
            ConsumeImport<LocalImportFilePrepareFailed>(LocalImportTopology.LocalImportFilePrepareFailedConsumer, stoppingToken),
            ConsumeDownload<UploadCompleted>(DownloadTopology.LocalImportUploadCompletedConsumer, stoppingToken),
            ConsumeDownload<UploadFailed>(DownloadTopology.LocalImportUploadFailedConsumer, stoppingToken),
            ConsumeDownload<UploadedObjectDeleted>(DownloadTopology.LocalImportUploadedObjectDeletedConsumer, stoppingToken),
            ConsumeDownload<UploadedObjectDeleteFailed>(DownloadTopology.LocalImportUploadedObjectDeleteFailedConsumer, stoppingToken)
        };

        logger.LogInformation("Subscribed to {Count} local import event consumers.", consumers.Length);
        return Task.WhenAll(consumers);
    }

    private Task ConsumeImport<TEvent>(string consumerName, CancellationToken stoppingToken)
        where TEvent : class, IFlowMessage
        => consumer.ConsumePullAsync<TEvent>(
            stream: ImportStream,
            consumer: ConsumerName.From(consumerName),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private Task ConsumeDownload<TEvent>(string consumerName, CancellationToken stoppingToken)
        where TEvent : class, IFlowMessage
        => consumer.ConsumePullAsync<TEvent>(
            stream: DownloadStream,
            consumer: ConsumerName.From(consumerName),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync<TEvent>(IJsMessageContext<TEvent> context)
        where TEvent : class, IFlowMessage
    {
        var evt = context.Message;
        try
        {
            if (!evt.OperationKey.StartsWith("local-import/", StringComparison.Ordinal))
            {
                await context.AckAsync();
                return;
            }

            await flows.SendMessage(evt.JobId.ToString("N"), evt, idempotencyKey: evt.OperationKey);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed handling local import event {EventType} for BatchId {BatchId}; nacking.",
                typeof(TEvent).Name,
                evt.JobId);
            await context.NackAsync();
        }
    }
}
