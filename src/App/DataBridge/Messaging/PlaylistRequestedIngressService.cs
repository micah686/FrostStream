using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Consumes <see cref="PlaylistRequested"/> from JetStream. Persists/reuses the playlist
/// row, then emits <see cref="FetchPlaylistMetadataCommand"/> for the Worker to resolve
/// the entry list. Acks only after the row is committed and the next-stage command is
/// published — JetStream redelivery on transient failure restarts the whole step.
/// </summary>
public sealed class PlaylistRequestedIngressService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<PlaylistRequestedIngressService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        consumer.ConsumePullAsync<PlaylistRequested>(
            stream: StreamName.From(PlaylistTopology.StreamNameValue),
            consumer: ConsumerName.From(PlaylistTopology.PlaylistRequestedConsumer),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<PlaylistRequested> context)
    {
        var request = context.Message;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            var playlists = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();

            if (await jobs.IsMessageProcessedAsync(request.MessageId))
            {
                await context.AckAsync();
                return;
            }

            var upsert = await playlists.CreateOrReuseAsync(request);

            var fetchCmd = new FetchPlaylistMetadataCommand
            {
                PlaylistId = upsert.Playlist.PlaylistId,
                CorrelationId = upsert.Playlist.CorrelationId,
                CausationId = request.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"playlist/{upsert.Playlist.PlaylistId:N}/fetch-metadata/attempt/1",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                SourceUrl = upsert.Playlist.SourceUrl,
                PageStartIndex = 1,
                PageSize = FetchPlaylistMetadataCommandDefaults.PageSize
            };

            await publisher.PublishAsync(
                PlaylistSubjects.FetchPlaylistMetadataCommand,
                fetchCmd,
                messageId: fetchCmd.MessageId.ToString("N"));

            await jobs.MarkMessageProcessedAsync(request.MessageId, request.OperationKey, request.PlaylistId);

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling PlaylistRequested for PlaylistId {PlaylistId}; nacking for redelivery", request.PlaylistId);
            await context.NackAsync();
        }
    }
}
