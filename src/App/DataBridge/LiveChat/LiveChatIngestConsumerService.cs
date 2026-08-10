using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.LiveChat;

/// <summary>
/// JetStream consumer for <see cref="LiveChatIngestRequested"/> on the background-jobs stream.
/// Registered only when live chat is enabled; when disabled nothing publishes the subject
/// either, and enabling later hydrates history via the backfill job rather than stale stream
/// messages (7-day MaxAge). Ingestion is idempotent, so a nack/redelivery never duplicates rows.
/// </summary>
public sealed class LiveChatIngestConsumerService(
    IJetStreamConsumer consumer,
    IServiceScopeFactory scopeFactory,
    ILogger<LiveChatIngestConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Subscribed to live chat ingest requests on stream {Stream}.", Stream.Value);
        return consumer.ConsumePullAsync<LiveChatIngestRequested>(
            Stream,
            ConsumerName.From(BackgroundJobsTopology.LiveChatIngestConsumer),
            HandleAsync,
            options: null,
            cancellationToken: stoppingToken);
    }

    private async Task HandleAsync(IJsMessageContext<LiveChatIngestRequested> context)
    {
        var request = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var ingest = scope.ServiceProvider.GetRequiredService<LiveChatIngestService>();
            await ingest.IngestAsync(request, CancellationToken.None);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Live chat ingest failed for MediaGuid {MediaGuid} ({Path}); nacking.",
                request.MediaGuid, request.ChatBlobPath);
            await context.NackAsync();
        }
    }
}
