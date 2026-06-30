using Cleipnir.ResilientFunctions.Domain.Exceptions;
using DataBridge.Data;
using DataBridge.Flows;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class LocalMediaImportRequestedIngressService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    LocalMediaImportFlows flows,
    ILogger<LocalMediaImportRequestedIngressService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        consumer.ConsumePullAsync<LocalMediaImportRequested>(
            stream: StreamName.From(LocalImportTopology.StreamNameValue),
            consumer: ConsumerName.From(LocalImportTopology.LocalMediaImportRequestedConsumer),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<LocalMediaImportRequested> context)
    {
        var request = context.Message;
        try
        {
            await scopeFactory.WithScopedAsync<ILocalImportRepository>(
                repo => repo.CreateBatchIfMissingAsync(request));

            try
            {
                await flows.Run(request.BatchId.ToString("N"), request);
            }
            catch (InvocationSuspendedException)
            {
                logger.LogWarning(
                    "LocalMediaImportFlow suspended after start for BatchId {BatchId}; acknowledging request.",
                    request.BatchId);
            }

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling LocalMediaImportRequested for BatchId {BatchId}; nacking.", request.BatchId);
            await context.NackAsync();
        }
    }
}
