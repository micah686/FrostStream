using Cleipnir.ResilientFunctions.Domain.Exceptions;
using FrostStream.ApplicationContracts;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class DownloadGroupRequestedIngressService(
    IDurableJobConsumer consumer,
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

    internal async Task HandleAsync(IDurableMessageContext<DownloadGroupRequested> context)
    {
        var request = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>();
            if (!startupState.IsReady) { await context.NackAsync(); return; }
            if (!await repository.AcceptGroupRequestAsync(request,startupState.GenerationStartedAt))
            {
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
