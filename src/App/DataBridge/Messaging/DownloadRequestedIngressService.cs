using Cleipnir.ResilientFunctions.Domain.Exceptions;
using Conduit.NATS;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>Creates a V2 job/run and starts its immutable Cleipnir instance.</summary>
public sealed class DownloadRequestedIngressService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    DownloadJobV2Flows flows,
    DownloadFlowStartupState startupState,
    ILogger<DownloadRequestedIngressService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        consumer.ConsumePullAsync<DownloadRequested>(
            StreamName.From(DownloadTopology.StreamNameValue),
            ConsumerName.From(DownloadTopology.DownloadRequestedConsumer),
            HandleAsync,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<DownloadRequested> context)
    {
        var request = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var legacy = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            if (await legacy.IsMessageProcessedAsync(request.MessageId))
            {
                await context.AckAsync();
                return;
            }

            var repository = scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>();
            // A durable request produced before this DataBridge generation is visible but stopped;
            // it is never silently resumed merely because the service came back.
            var autoStart = request.OccurredAt >= startupState.GenerationStartedAt;
            var run = await repository.CreateInitialRunAsync(request, autoStart);
            if (run is not null)
            {
                try
                {
                    await flows.Run(DownloadFlowInstance.Job(request.JobId, run.RunId), run);
                }
                catch (InvocationSuspendedException)
                {
                    // Waiting for a Worker result is the expected handoff point.
                }
            }

            await legacy.MarkMessageProcessedAsync(request.MessageId, request.OperationKey, request.JobId);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed accepting Download V2 request for JobId {JobId}", request.JobId);
            await context.NackAsync();
        }
    }
}
