using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// DataBridge-side JetStream consumers for the playlist pipeline. Handles the Worker's
/// metadata-fetched / metadata-fetch-failed events and the internal
/// <c>ProcessPlaylistStagedEntriesCommand</c> that drains the staging table and fans out
/// per-entry <see cref="DownloadRequested"/> messages.
/// </summary>
public sealed class PlaylistEventsConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<PlaylistEventsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(PlaylistTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            consumer.ConsumePullAsync<PlaylistMetadataFetched>(
                stream: Stream,
                consumer: ConsumerName.From(PlaylistTopology.PlaylistMetadataFetchedConsumer),
                handler: HandleMetadataFetchedAsync,
                options: null,
                cancellationToken: stoppingToken),
            consumer.ConsumePullAsync<PlaylistMetadataFetchFailed>(
                stream: Stream,
                consumer: ConsumerName.From(PlaylistTopology.PlaylistMetadataFetchFailedConsumer),
                handler: HandleMetadataFetchFailedAsync,
                options: null,
                cancellationToken: stoppingToken),
            consumer.ConsumePullAsync<ProcessPlaylistStagedEntriesCommand>(
                stream: Stream,
                consumer: ConsumerName.From(PlaylistTopology.ProcessStagedEntriesConsumer),
                handler: HandleProcessStagedEntriesAsync,
                options: null,
                cancellationToken: stoppingToken)
        };

        logger.LogInformation("Subscribed to {Count} playlist event consumers on stream {Stream}.", consumers.Length, Stream.Value);
        return Task.WhenAll(consumers);
    }

    private async Task HandleMetadataFetchedAsync(IJsMessageContext<PlaylistMetadataFetched> context)
    {
        var evt = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            var playlists = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();

            if (!await jobs.IsMessageProcessedAsync(evt.MessageId))
            {
                await playlists.ApplyMetadataFetchedAsync(evt.PlaylistId, evt);
                await playlists.WriteStagingEntriesAsync(evt.PlaylistId, evt.Entries);
                await jobs.MarkMessageProcessedAsync(evt.MessageId, evt.OperationKey, evt.PlaylistId);
            }

            // Always re-publish the process-staged-entries command — re-publishing is
            // idempotent (each entry is fanned out only once thanks to the unique
            // (playlist_id, entry_url) constraint), and a duplicate event still needs to
            // make sure the staging drain happens.
            var cmd = new ProcessPlaylistStagedEntriesCommand
            {
                PlaylistId = evt.PlaylistId,
                CorrelationId = evt.CorrelationId,
                CausationId = evt.MessageId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"playlist/{evt.PlaylistId:N}/process-staged",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1
            };
            await publisher.PublishAsync(
                PlaylistSubjects.ProcessPlaylistStagedEntriesCommand,
                cmd,
                messageId: cmd.MessageId.ToString("N"));

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling PlaylistMetadataFetched for PlaylistId {PlaylistId}", evt.PlaylistId);
            await context.NackAsync();
        }
    }

    private async Task HandleMetadataFetchFailedAsync(IJsMessageContext<PlaylistMetadataFetchFailed> context)
    {
        var evt = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            var playlists = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();

            if (await jobs.IsMessageProcessedAsync(evt.MessageId))
            {
                await context.AckAsync();
                return;
            }

            await playlists.UpdateStateAsync(evt.PlaylistId, PlaylistState.Failed);
            await jobs.MarkMessageProcessedAsync(evt.MessageId, evt.OperationKey, evt.PlaylistId);

            logger.LogWarning(
                "PlaylistMetadataFetchFailed for PlaylistId {PlaylistId}: {FailureKind} {ErrorCode} {ErrorMessage}",
                evt.PlaylistId, evt.FailureKind, evt.ErrorCode, evt.ErrorMessage);

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling PlaylistMetadataFetchFailed for PlaylistId {PlaylistId}", evt.PlaylistId);
            await context.NackAsync();
        }
    }

    private async Task HandleProcessStagedEntriesAsync(IJsMessageContext<ProcessPlaylistStagedEntriesCommand> context)
    {
        var cmd = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var playlists = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();

            var playlist = await playlists.GetByIdAsync(cmd.PlaylistId);
            if (playlist is null)
            {
                logger.LogWarning("ProcessPlaylistStagedEntries: playlist {PlaylistId} not found", cmd.PlaylistId);
                await context.AckAsync();
                return;
            }

            var entries = await playlists.ListStagingEntriesAsync(cmd.PlaylistId);
            foreach (var entry in entries)
            {
                var jobId = Guid.NewGuid();
                var fanOut = new FanOutEntryRequest(
                    PlaylistId: playlist.PlaylistId,
                    CorrelationId: playlist.CorrelationId,
                    JobId: jobId,
                    PlaylistIndex: entry.PlaylistIndex,
                    EntryUrl: entry.EntryUrl,
                    EntryTitle: entry.EntryTitle,
                    RequestedBy: playlist.RequestedBy,
                    StorageKey: playlist.StorageKey);

                await playlists.FanOutEntryAsync(fanOut);

                var downloadRequested = new DownloadRequested
                {
                    JobId = jobId,
                    CorrelationId = playlist.CorrelationId,
                    CausationId = cmd.MessageId,
                    MessageId = Guid.NewGuid(),
                    OperationKey = $"job/{jobId:N}/requested",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = 1,
                    SourceUrl = entry.EntryUrl,
                    RequestedBy = playlist.RequestedBy,
                    StorageKey = playlist.StorageKey ?? "default"
                };

                await publisher.PublishAsync(
                    DownloadSubjects.DownloadRequested,
                    downloadRequested,
                    messageId: downloadRequested.MessageId.ToString("N"));
            }

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling ProcessPlaylistStagedEntries for PlaylistId {PlaylistId}", cmd.PlaylistId);
            await context.NackAsync();
        }
    }
}
