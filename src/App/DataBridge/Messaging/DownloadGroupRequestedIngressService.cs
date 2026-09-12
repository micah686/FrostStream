using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Application;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class DownloadGroupRequestedIngressService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadGroupRequestedIngressService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        consumer.ConsumePullAsync<DownloadGroupRequested>(
            StreamName.From(DownloadTopology.StreamNameValue),
            ConsumerName.From(DownloadTopology.GroupRequestedConsumer),
            context => HandleAsync(context, stoppingToken),
            cancellationToken: stoppingToken);

    private Task HandleAsync(IJsMessageContext<DownloadGroupRequested> context, CancellationToken cancellationToken)
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
