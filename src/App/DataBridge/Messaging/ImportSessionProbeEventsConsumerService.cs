using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class ImportSessionProbeEventsConsumerService(
    IJetStreamConsumer consumer,
    ImportSessionRequestReplyService sessionService,
    ILogger<ImportSessionProbeEventsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(LocalImportTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            Consume<ImportSessionItemsProbed>(LocalImportTopology.ImportSessionItemsProbedConsumer, HandleProbedAsync, stoppingToken),
            Consume<ImportSessionItemsProbeFailed>(LocalImportTopology.ImportSessionItemsProbeFailedConsumer, HandleProbeFailedAsync, stoppingToken),
            Consume<ImportSessionItemEnriched>(LocalImportTopology.ImportSessionItemEnrichedConsumer, HandleEnrichedAsync, stoppingToken),
            Consume<ImportSessionItemEnrichFailed>(LocalImportTopology.ImportSessionItemEnrichFailedConsumer, HandleEnrichFailedAsync, stoppingToken)
        };

        logger.LogInformation("Subscribed to {Count} import-session probe/enrich event consumers.", consumers.Length);
        return Task.WhenAll(consumers);
    }

    private Task Consume<TEvent>(
        string consumerName,
        Func<TEvent, Task> handler,
        CancellationToken stoppingToken)
        where TEvent : class, IFlowMessage
        => consumer.ConsumePullAsync<TEvent>(
            stream: Stream,
            consumer: ConsumerName.From(consumerName),
            handler: async context =>
            {
                try
                {
                    await handler(context.Message);
                    await context.AckAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed handling import-session probe event {EventType}; nacking.", typeof(TEvent).Name);
                    await context.NackAsync();
                }
            },
            options: null,
            cancellationToken: stoppingToken);

    private Task HandleProbedAsync(ImportSessionItemsProbed message)
        => sessionService.HandleItemsProbedAsync(message);

    private Task HandleProbeFailedAsync(ImportSessionItemsProbeFailed message)
        => sessionService.HandleItemsProbeFailedAsync(message);

    private Task HandleEnrichedAsync(ImportSessionItemEnriched message)
        => sessionService.HandleItemEnrichedAsync(message);

    private Task HandleEnrichFailedAsync(ImportSessionItemEnrichFailed message)
        => sessionService.HandleItemEnrichFailedAsync(message);
}
