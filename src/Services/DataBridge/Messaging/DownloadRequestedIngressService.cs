using DataBridge.Data;
using DataBridge.Flows;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Consumes <see cref="DownloadRequested"/> from JetStream and starts a Cleipnir
/// <see cref="DownloadArchiveFlow"/> per <see cref="DownloadRequested.JobId"/>.
///
/// Ack ordering is critical: the JetStream message is only acknowledged AFTER the
/// job row is written, the flow is started, and the dedupe row is persisted. If any
/// step fails, the message is re-delivered until <c>MaxDeliver</c> (configured in
/// <see cref="DownloadTopology"/>) is exhausted.
/// </summary>
public sealed class DownloadRequestedIngressService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    DownloadArchiveFlows flows,
    ILogger<DownloadRequestedIngressService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        consumer.ConsumePullAsync<DownloadRequested>(
            stream: StreamName.From(DownloadTopology.StreamNameValue),
            consumer: ConsumerName.From(DownloadTopology.DownloadRequestedConsumer),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<DownloadRequested> context)
    {
        var request = context.Message;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();

            if (await jobs.IsMessageProcessedAsync(request.MessageId))
            {
                await context.AckAsync();
                return;
            }

            await jobs.CreateJobIfMissingAsync(request);
            await jobs.RecordHistoryAsync(
                request.JobId,
                request.MessageId,
                request.OperationKey,
                nameof(DownloadRequested),
                payloadJson: null);

            // flows.Run is safe to call again for an existing instance id — Cleipnir
            // recognises the duplicate and no-ops.
            await flows.Run(request.JobId.ToString("N"), request);

            await jobs.MarkMessageProcessedAsync(request.MessageId, request.OperationKey, request.JobId);

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling DownloadRequested for JobId {JobId}; nacking for redelivery", request.JobId);
            await context.NackAsync();
        }
    }
}
