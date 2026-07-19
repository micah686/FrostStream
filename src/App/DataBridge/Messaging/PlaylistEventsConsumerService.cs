using Conduit.NATS;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Routes playlist-expansion Worker results to the one V2 group flow that requested them.
/// Per-entry persistence and fan-out live exclusively in <see cref="DownloadGroupV2Flow"/>.
/// Results for a missing, stopped, failed, or already-settled group are stale and acknowledged.
/// </summary>
public sealed class PlaylistEventsConsumerService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    DownloadGroupV2Flows groupFlows,
    ILogger<PlaylistEventsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(PlaylistTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            consumer.ConsumePullAsync<PlaylistMetadataFetched>(
                Stream,
                ConsumerName.From(PlaylistTopology.PlaylistMetadataFetchedConsumer),
                HandleAsync,
                cancellationToken: stoppingToken),
            consumer.ConsumePullAsync<PlaylistMetadataFetchFailed>(
                Stream,
                ConsumerName.From(PlaylistTopology.PlaylistMetadataFetchFailedConsumer),
                HandleAsync,
                cancellationToken: stoppingToken)
        };

        logger.LogInformation("Subscribed to {Count} V2 playlist-expansion result consumers.", consumers.Length);
        return Task.WhenAll(consumers);
    }

    private Task HandleAsync(IJsMessageContext<PlaylistMetadataFetched> context)
        => ForwardAsync(context, context.Message);

    private Task HandleAsync(IJsMessageContext<PlaylistMetadataFetchFailed> context)
        => ForwardAsync(context, context.Message);

    private async Task ForwardAsync<T>(IJsMessageContext<T> context, T message)
        where T : class, IPlaylistFlowMessage
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var group = await scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>()
                .DownloadGroups.AsNoTracking()
                .Where(x => x.CorrelationId == message.CorrelationId)
                .Select(x => new { x.GroupId, x.Status })
                .FirstOrDefaultAsync();

            if (group?.Status == DownloadGroupStatus.Expanding)
            {
                await groupFlows.SendMessage(
                    DownloadFlowInstance.Group(group.GroupId),
                    message,
                    idempotencyKey: message.OperationKey);
            }
            else
            {
                logger.LogDebug(
                    "Ignoring stale {Event} for playlist group correlation {CorrelationId}.",
                    typeof(T).Name,
                    message.CorrelationId);
            }

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed routing {Event} for playlist group correlation {CorrelationId}.",
                typeof(T).Name,
                message.CorrelationId);
            await context.NackAsync();
        }
    }
}
