using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class MetadataRebuildConsumerService(
    IMessageBus messageBus,
    IMetadataRebuildCoordinator rebuildCoordinator,
    ILogger<MetadataRebuildConsumerService> logger) : BackgroundService
{
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<MetadataSyncRebuildRequestMessage>(
            MetadataSyncSubjects.SyncRebuild,
            HandleAsync,
            queueGroup: MetadataSubjects.SearchQueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to metadata sync rebuild subject.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.StopAsync(cancellationToken);
            await _subscription.DisposeAsync();
            _subscription = null;
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(IMessageContext<MetadataSyncRebuildRequestMessage> context)
    {
        var result = rebuildCoordinator.StartRebuild("manual request");
        await context.RespondAsync(new MetadataSyncRebuildResponseMessage
        {
            Accepted = result.Accepted,
            ErrorMessage = result.ErrorMessage
        });
    }
}
