using DataBridge;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class PlaylistQueryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
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

        logger.LogInformation("Subscribed to playlist query subjects.");
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

            await context.RespondAsync(new PlaylistGetResponseMessage
            {
                Success = true,
                Playlist = MapDetail(detail)
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
            await context.RespondAsync(new PlaylistListResponseMessage
            {
                Success = true,
                Items = summaries.Select(MapSummary).ToArray()
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
                MediaGuid = item.MediaGuid
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
