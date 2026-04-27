using DataBridge.Data;
using DataBridge.Flows;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Consumes <see cref="DownloadRequested"/> from WebAPI and starts a Cleipnir
/// <see cref="DownloadArchiveFlow"/> per <see cref="DownloadRequested.JobId"/>.
/// Modelled on <see cref="StorageCrudConsumerService"/>.
/// </summary>
public sealed class DownloadRequestedIngressService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    DownloadArchiveFlows flows,
    ILogger<DownloadRequestedIngressService> logger) : BackgroundService
{
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<DownloadRequested>(
            DownloadSubjects.DownloadRequested,
            HandleDownloadRequestedAsync,
            queueGroup: "databridge-downloads",
            cancellationToken: stoppingToken));

        logger.LogInformation("Subscribed to {Subject}", DownloadSubjects.DownloadRequested);

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
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task HandleDownloadRequestedAsync(IMessageContext<DownloadRequested> context)
    {
        var request = context.Message;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();

            if (await jobs.IsMessageProcessedAsync(request.MessageId))
            {
                return;
            }

            await jobs.CreateJobIfMissingAsync(request);
            await jobs.RecordHistoryAsync(
                request.JobId,
                request.MessageId,
                request.OperationKey,
                nameof(DownloadRequested),
                payloadJson: null);

            // Cleipnir's Flows.Run is idempotent against existing instance ids — running the
            // same JobId twice is safe; the second call no-ops once the flow is already started.
            await flows.Run(request.JobId.ToString("N"), request);

            await jobs.MarkMessageProcessedAsync(request.MessageId, request.OperationKey, request.JobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling DownloadRequested for JobId {JobId}", request.JobId);
            throw;
        }
    }
}
