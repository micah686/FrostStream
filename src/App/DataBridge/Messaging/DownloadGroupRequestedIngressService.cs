using Cleipnir.ResilientFunctions.Domain.Exceptions;
using Conduit.NATS;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class DownloadGroupRequestedIngressService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    DownloadGroupV2Flows flows,
    DownloadFlowStartupState startupState,
    ILogger<DownloadGroupRequestedIngressService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        consumer.ConsumePullAsync<DownloadGroupRequested>(
            StreamName.From(DownloadTopology.StreamNameValue),
            ConsumerName.From(DownloadTopology.GroupRequestedConsumer),
            HandleAsync,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<DownloadGroupRequested> context)
    {
        var request = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>();
            await repository.CreateGroupIfMissingAsync(request);

            // JetStream may still contain a request published by the prior process generation.
            // Make it visible to the user, but never turn service startup into an implicit Start.
            if (request.OccurredAt < startupState.GenerationStartedAt)
            {
                // A direct group already contains its only child, so persist that child as Stopped
                // as well. It then appears on the Jobs page and the user can explicitly Start a
                // fresh run. Collection groups cannot invent children until discovery is requested.
                if (request.Kind == DownloadGroupKind.Direct && request.DirectRequest is { } direct)
                    await repository.CreateInitialRunAsync(direct, autoStart: false);
                await repository.SetGroupStatusAsync(request.GroupId, DownloadGroupStatus.Stopped);
                await context.AckAsync();
                return;
            }

            try
            {
                await flows.Run(DownloadFlowInstance.Group(request.GroupId), request);
            }
            catch (InvocationSuspendedException)
            {
            }
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed accepting Download V2 group {GroupId}", request.GroupId);
            await context.NackAsync();
        }
    }
}
