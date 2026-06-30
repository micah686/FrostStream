using Cleipnir.ResilientFunctions.Domain.Exceptions;
using System.Text.Json;
using DataBridge.Data;
using DataBridge.Flows;
using Conduit.NATS;
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
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

            if (await jobs.IsMessageProcessedAsync(request.MessageId))
            {
                await context.AckAsync();
                return;
            }

            await jobs.CreateJobIfMissingAsync(request);

            // The first invocation normally suspends while waiting for Worker events.
            // Treat that as a successful handoff to Cleipnir, not as a NATS failure.
            // flows.Run is safe to call again for an existing instance id — Cleipnir
            // recognises the duplicate and no-ops.
            try
            {
                await flows.Run(request.JobId.ToString("N"), request);
            }
            catch (InvocationSuspendedException)
            {
                logger.LogWarning(
                    "DownloadArchiveFlow suspended after start for JobId {JobId}; acknowledging DownloadRequested.",
                    request.JobId);
            }

            await using var tx = await db.Database.BeginTransactionAsync();
            if (!await jobs.TryMarkMessageProcessedAsync(request.MessageId, request.OperationKey, request.JobId))
            {
                await tx.CommitAsync();
                await context.AckAsync();
                return;
            }

            await jobs.RecordHistoryAsync(
                request.JobId,
                request.MessageId,
                request.OperationKey,
                nameof(DownloadRequested),
                JsonSerializer.Serialize(request));

            await tx.CommitAsync();

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling DownloadRequested for JobId {JobId}; nacking for redelivery", request.JobId);
            await context.NackAsync();
        }
    }
}
