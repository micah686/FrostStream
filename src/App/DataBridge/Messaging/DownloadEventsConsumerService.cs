using System.Text.Json;
using Conduit.NATS;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Validates Worker results against the authoritative current RunId/DispatchId before handing
/// them to Cleipnir. Stale and duplicate results are acknowledged but can never advance a run.
/// </summary>
public sealed class DownloadEventsConsumerService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    DownloadJobV2Flows flows,
    ILogger<DownloadEventsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName DownloadStream = StreamName.From(DownloadTopology.StreamNameValue);
    private static readonly StreamName ArtifactStream = StreamName.From(ArtifactStorageTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new[]
        {
            Consume<MetadataFetched>(DownloadStream, DownloadTopology.MetadataFetchedConsumer, stoppingToken),
            Consume<MetadataFetchFailed>(DownloadStream, DownloadTopology.MetadataFetchFailedConsumer, stoppingToken),
            Consume<DownloadCompleted>(DownloadStream, DownloadTopology.DownloadCompletedConsumer, stoppingToken),
            Consume<DownloadFailed>(DownloadStream, DownloadTopology.DownloadFailedConsumer, stoppingToken),
            Consume<UploadCompleted>(ArtifactStream, ArtifactStorageTopology.DownloadUploadCompletedConsumer, stoppingToken),
            Consume<UploadFailed>(ArtifactStream, ArtifactStorageTopology.DownloadUploadFailedConsumer, stoppingToken),
            Consume<TempFileDeleted>(ArtifactStream, ArtifactStorageTopology.DownloadTempDeletedConsumer, stoppingToken),
            Consume<TempFileDeleteFailed>(ArtifactStream, ArtifactStorageTopology.DownloadTempDeleteFailedConsumer, stoppingToken),
            Consume<UploadedObjectDeleted>(ArtifactStream, ArtifactStorageTopology.DownloadObjectDeletedConsumer, stoppingToken),
            Consume<UploadedObjectDeleteFailed>(ArtifactStream, ArtifactStorageTopology.DownloadObjectDeleteFailedConsumer, stoppingToken)
        };
        logger.LogInformation("Subscribed to {Count} Download V2 result consumers.", tasks.Length);
        return Task.WhenAll(tasks);
    }

    private Task Consume<T>(StreamName stream, string durable, CancellationToken stoppingToken)
        where T : class, IFlowMessage
        => consumer.ConsumePullAsync<T>(stream, ConsumerName.From(durable), HandleAsync, cancellationToken: stoppingToken);

    private async Task HandleAsync<T>(IJsMessageContext<T> context) where T : class, IFlowMessage
    {
        var evt = context.Message;
        try
        {
            if (evt.OperationKey.StartsWith("local-import", StringComparison.Ordinal))
            {
                await context.AckAsync();
                return;
            }

            var execution = Execution(evt);
            if (execution is null)
            {
                logger.LogWarning("Ignoring V2 result {Type} without execution identity.", typeof(T).Name);
                await context.AckAsync();
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var v2 = scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>();
            var legacy = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            if (!await v2.CanAcceptWorkerEventAsync(execution)
                || await legacy.IsMessageProcessedAsync(evt.MessageId))
            {
                await context.AckAsync();
                return;
            }

            await v2.ReleaseLeaseAsync(execution.DispatchId,
                FailureKindOf(evt) is FailureKind.Stopped or FailureKind.Cancelled
                    ? DownloadWorkerLeaseStatus.Stopped
                    : DownloadWorkerLeaseStatus.Released);
            await legacy.RecordHistoryAsync(evt.JobId, evt.MessageId, evt.OperationKey,
                typeof(T).Name, JsonSerializer.Serialize(evt));
            await flows.SendMessage(
                DownloadFlowInstance.Job(evt.JobId, execution.RunId),
                evt,
                idempotencyKey: evt.OperationKey);
            await legacy.MarkMessageProcessedAsync(evt.MessageId, evt.OperationKey, evt.JobId);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling V2 result {Type} for JobId {JobId}", typeof(T).Name, evt.JobId);
            await context.NackAsync();
        }
    }

    private static DownloadExecutionIdentity? Execution(IFlowMessage message) => message switch
    {
        MetadataFetched x => x.Execution,
        MetadataFetchFailed x => x.Execution,
        DownloadCompleted x => x.Execution,
        DownloadFailed x => x.Execution,
        UploadCompleted x => x.Execution,
        UploadFailed x => x.Execution,
        TempFileDeleted x => x.Execution,
        TempFileDeleteFailed x => x.Execution,
        UploadedObjectDeleted x => x.Execution,
        UploadedObjectDeleteFailed x => x.Execution,
        _ => null
    };

    private static FailureKind? FailureKindOf(IFlowMessage message) => message switch
    {
        MetadataFetchFailed x => x.FailureKind,
        DownloadFailed x => x.FailureKind,
        UploadFailed x => x.FailureKind,
        TempFileDeleteFailed x => x.FailureKind,
        UploadedObjectDeleteFailed x => x.FailureKind,
        _ => null
    };
}

/// <summary>Consumes advisory stage telemetry; leases remain authoritative in PostgreSQL.</summary>
public sealed class DownloadStageTelemetryConsumerService(
    IJetStreamConsumer consumer,
    ILogger<DownloadStageTelemetryConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(DownloadTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Consume<DownloadStageStarted>(DownloadTopology.StageStartedConsumer, stoppingToken),
        Consume<DownloadStageHeartbeat>(DownloadTopology.StageHeartbeatConsumer, stoppingToken),
        Consume<DownloadStageSucceeded>(DownloadTopology.StageSucceededConsumer, stoppingToken),
        Consume<DownloadStageFailed>(DownloadTopology.StageFailedConsumer, stoppingToken),
        Consume<DownloadStageStopped>(DownloadTopology.StageStoppedConsumer, stoppingToken));

    private Task Consume<T>(string durable, CancellationToken ct) where T : class, IDownloadStageEvent
        => consumer.ConsumePullAsync<T>(Stream, ConsumerName.From(durable), async context =>
        {
            logger.LogDebug("Worker {Worker} reported {Event} for JobId {JobId} RunId {RunId} Stage {Stage} Attempt {Attempt}",
                context.Message.WorkerInstanceId, typeof(T).Name, context.Message.Execution.JobId,
                context.Message.Execution.RunId, context.Message.Execution.Stage, context.Message.Execution.Attempt);
            await context.AckAsync();
        }, cancellationToken: ct);
}

/// <summary>Routes supervised channel-expansion results to the matching durable group flow.</summary>
public sealed class DownloadGroupExpansionEventsConsumerService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    DownloadGroupV2Flows flows,
    ILogger<DownloadGroupExpansionEventsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(DownloadTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        consumer.ConsumePullAsync<DownloadGroupExpansionSucceeded>(
            Stream,
            ConsumerName.From(DownloadTopology.GroupExpansionSucceededConsumer),
            HandleAsync,
            cancellationToken: stoppingToken),
        consumer.ConsumePullAsync<DownloadGroupExpansionFailed>(
            Stream,
            ConsumerName.From(DownloadTopology.GroupExpansionFailedConsumer),
            HandleAsync,
            cancellationToken: stoppingToken));

    private Task HandleAsync(IJsMessageContext<DownloadGroupExpansionSucceeded> context)
        => ForwardAsync(context, context.Message.GroupId, context.Message.CorrelationId,
            context.Message.OperationKey, context.Message);

    private Task HandleAsync(IJsMessageContext<DownloadGroupExpansionFailed> context)
        => ForwardAsync(context, context.Message.GroupId, context.Message.CorrelationId,
            context.Message.OperationKey, context.Message);

    private async Task ForwardAsync<T>(
        IJsMessageContext<T> context,
        Guid groupId,
        Guid correlationId,
        string operationKey,
        T message) where T : class
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var active = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .IsGroupExpansionAllowedAsync(correlationId);
            if (active)
            {
                await flows.SendMessage(
                    DownloadFlowInstance.Group(groupId), message, idempotencyKey: operationKey);
            }
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed routing V2 expansion result for GroupId {GroupId}.", groupId);
            await context.NackAsync();
        }
    }
}
