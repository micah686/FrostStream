using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Application;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>Creates a V2 job/run and starts its immutable Cleipnir instance.</summary>
public sealed class DownloadRequestedIngressService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadRequestedIngressService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        consumer.ConsumePullAsync<DownloadRequested>(
            StreamName.From(DownloadTopology.StreamNameValue),
            ConsumerName.From(DownloadTopology.DownloadRequestedConsumer),
            context => HandleAsync(context, stoppingToken),
            cancellationToken: stoppingToken);

    private Task HandleAsync(IJsMessageContext<DownloadRequested> context, CancellationToken cancellationToken)
    {
        return JetStreamWorkAdapter.ExecuteAsync(
            context,
            async (request, ct) =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IDownloadWorkflowIngress>()
                    .AcceptAsync(request, ct);
            },
            logger,
            cancellationToken);
    }
}
