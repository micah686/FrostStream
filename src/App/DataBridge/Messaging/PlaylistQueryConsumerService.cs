using System.Text.Json;
using DataBridge;
using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace DataBridge.Messaging;

public sealed class PlaylistQueryConsumerService(
    IMessageBus messageBus,
    IJetStreamPublisher publisher,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<PlaylistQueryConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-playlist-queries";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<PlaylistGetRequestMessage>(
            messageBus,
            PlaylistSubjects.PlaylistGet,
            HandleGetAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<PlaylistListRequestMessage>(
            messageBus,
            PlaylistSubjects.PlaylistList,
            HandleListAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<PlaylistItemForceQueueRequestMessage>(
            messageBus,
            PlaylistSubjects.PlaylistItemForceQueue,
            HandleForceQueueAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to playlist query subjects.");
    }

    private async Task HandleForceQueueAsync(IMessageContext<PlaylistItemForceQueueRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var playlists = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();

            var playlist = await playlists.GetByIdAsync(msg.PlaylistId);
            if (playlist is null
                || (!string.IsNullOrWhiteSpace(msg.RequestedBy)
                    && !string.IsNullOrWhiteSpace(playlist.RequestedBy)
                    && playlist.RequestedBy != msg.RequestedBy))
            {
                await context.RespondAsync(NotFoundForceQueue($"Playlist '{msg.PlaylistId}' was not found."));
                return;
            }

            var entryUrl = await playlists.RequeuePlaylistItemAsync(msg.PlaylistId, msg.JobId);
            if (entryUrl is null)
            {
                await context.RespondAsync(NotFoundForceQueue($"Playlist item '{msg.JobId}' was not found."));
                return;
            }

            var downloadRequested = new DownloadRequested
            {
                JobId = msg.JobId,
                CorrelationId = playlist.CorrelationId,
                CausationId = null,
                MessageId = Guid.NewGuid(),
                OperationKey = $"job/{msg.JobId:N}/force-requested",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                SourceUrl = entryUrl,
                RequestedBy = playlist.RequestedBy,
                StorageKey = playlist.StorageKey ?? "default",
                ForceDownload = true,
                YtDlpOptions = DeserializeYtDlpOptions(playlist.YtDlpOptionsJson),
                CookieSecretPath = playlist.CookieSecretPath,
                Priority = playlist.Priority,
                FetchComments = playlist.FetchComments,
                EncodeAudioRendition = playlist.EncodeForPlaylist,
                SourceKind = DownloadSourceKind.Playlist
            };

            await publisher.PublishAsync(
                DownloadSubjects.DownloadRequested,
                downloadRequested,
                messageId: downloadRequested.MessageId.ToString("N"));

            await context.RespondAsync(new ForceQueueOperationResponseMessage { Success = true, JobId = msg.JobId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed force-queueing playlist item {JobId} in {PlaylistId}", msg.JobId, msg.PlaylistId);
            await context.RespondAsync(new ForceQueueOperationResponseMessage
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to force-queue playlist item."
            });
        }
    }

    private static ForceQueueOperationResponseMessage NotFoundForceQueue(string message)
        => new() { Success = false, ErrorCode = "not_found", ErrorMessage = message };

    private static YtDlpOptions? DeserializeYtDlpOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<YtDlpOptions>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task HandleGetAsync(IMessageContext<PlaylistGetRequestMessage> context)
    {
        try
        {
            var detail = await WithPlaylists(playlists => playlists.GetDetailAsync(context.Message.PlaylistId));
            if (detail is null)
            {
                await context.RespondAsync(new PlaylistGetResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Playlist '{context.Message.PlaylistId}' was not found."
                });
                return;
            }

            var playlist = MapDetail(detail);
            playlist = await AttachNoteAsync(playlist, context.Message.OwnerSubject);

            await context.RespondAsync(new PlaylistGetResponseMessage
            {
                Success = true,
                Playlist = playlist
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling PlaylistGet for {PlaylistId}", context.Message.PlaylistId);
            await context.RespondAsync(new PlaylistGetResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal playlist service error."
            });
        }
    }

    private async Task HandleListAsync(IMessageContext<PlaylistListRequestMessage> context)
    {
        try
        {
            var summaries = await WithPlaylists(playlists => playlists.ListAsync(context.Message.PageSize, context.Message.PageOffset));
            var items = await AttachNotesAsync(summaries.Select(MapSummary).ToArray(), context.Message.OwnerSubject);
            await context.RespondAsync(new PlaylistListResponseMessage
            {
                Success = true,
                Items = items
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling PlaylistList");
            await context.RespondAsync(new PlaylistListResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal playlist service error."
            });
        }
    }

    private Task<TResult> WithPlaylists<TResult>(Func<IPlaylistsRepository, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private async Task<PlaylistDto> AttachNoteAsync(PlaylistDto item, string? ownerSubject)
    {
        var items = await AttachNotesAsync([item], ownerSubject);
        return items[0];
    }

    private async Task<IReadOnlyList<PlaylistDto>> AttachNotesAsync(
        IReadOnlyList<PlaylistDto> items,
        string? ownerSubject)
    {
        if (items.Count == 0 || string.IsNullOrWhiteSpace(ownerSubject))
            return items;

        var ids = items.Select(x => x.PlaylistId).ToArray();
        var notes = await scopeFactory.WithScopedAsync<IUserNotesRepository, IReadOnlyDictionary<Guid, string>>(
            repository => repository.GetPlaylistNotesAsync(ownerSubject, ids));

        return items
            .Select(x => x with { UserNote = notes.GetValueOrDefault(x.PlaylistId) })
            .ToArray();
    }

    private static PlaylistDto MapDetail(PlaylistDetail detail)
        => MapBase(
            detail.Playlist,
            detail.CompletedItems,
            detail.FailedItems,
            detail.PendingItems,
            detail.Items.Select(item => new PlaylistItemDto
            {
                PlaylistIndex = item.PlaylistIndex,
                JobId = item.JobId,
                EntryUrl = item.EntryUrl,
                EntryTitle = item.EntryTitle,
                JobState = item.JobState,
                MediaGuid = item.MediaGuid,
                IgnoredKeyword = item.IgnoredKeyword
            }).ToArray());

    private static PlaylistDto MapSummary(PlaylistSummary summary)
        => MapBase(summary.Playlist, summary.CompletedItems, summary.FailedItems, summary.PendingItems, items: null);

    private static PlaylistDto MapBase(
        PlaylistEntity playlist,
        int completed,
        int failed,
        int pending,
        IReadOnlyList<PlaylistItemDto>? items)
        => new()
        {
            PlaylistId = playlist.PlaylistId,
            CorrelationId = playlist.CorrelationId,
            State = playlist.State,
            SourceUrl = playlist.SourceUrl,
            RequestedBy = playlist.RequestedBy,
            StorageKey = playlist.StorageKey,
            ProviderPlaylistId = playlist.ProviderPlaylistId,
            Title = playlist.Title,
            TotalItems = playlist.TotalItems,
            CreatedAt = playlist.CreatedAt,
            UpdatedAt = playlist.UpdatedAt,
            CompletedAt = playlist.CompletedAt,
            LastScannedAt = playlist.LastScannedAt,
            CompletedItems = completed,
            FailedItems = failed,
            PendingItems = pending,
            Items = items
        };
}
