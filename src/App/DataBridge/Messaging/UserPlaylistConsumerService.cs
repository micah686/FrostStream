using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class UserPlaylistConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<UserPlaylistConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-user-playlists";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<UserPlaylistCreateRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistCreate, HandleCreateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserPlaylistUpdateRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistUpdate, HandleUpdateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserPlaylistDeleteRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistDelete, HandleDeleteAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserPlaylistGetRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistGet, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserPlaylistListRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistList, HandleListAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserPlaylistAddItemRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistAddItem, HandleAddItemAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserPlaylistRemoveItemRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistRemoveItem, HandleRemoveItemAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<UserPlaylistReorderItemsRequestMessage>(messageBus, PlaylistSubjects.UserPlaylistReorderItems, HandleReorderItemsAsync, QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to user playlist subjects.");
    }

    private async Task HandleCreateAsync(IMessageContext<UserPlaylistCreateRequestMessage> context)
    {
        try
        {
            var validation = ValidateOwnerAndName(context.Message.OwnerSubject, context.Message.Name);
            if (validation is not null)
            {
                await context.RespondAsync(Failure(validation.Value));
                return;
            }

            var detail = await WithRepo(repository => repository.CreateAsync(
                context.Message.OwnerSubject,
                context.Message.Name,
                context.Message.Description));
            await context.RespondAsync(await SuccessAsync(detail, context.Message.OwnerSubject));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed creating user playlist for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(Failure("internal_error", "Internal user playlist service error."));
        }
    }

    private async Task HandleUpdateAsync(IMessageContext<UserPlaylistUpdateRequestMessage> context)
    {
        try
        {
            var validation = ValidateOwnerAndName(context.Message.OwnerSubject, context.Message.Name);
            if (validation is not null)
            {
                await context.RespondAsync(Failure(validation.Value));
                return;
            }

            var detail = await WithRepo(repository => repository.UpdateAsync(
                context.Message.OwnerSubject,
                context.Message.PlaylistId,
                context.Message.Name,
                context.Message.Description));
            await context.RespondAsync(detail is null
                ? Failure("not_found", $"Playlist '{context.Message.PlaylistId}' was not found.")
                : await SuccessAsync(detail, context.Message.OwnerSubject));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating user playlist {PlaylistId}.", context.Message.PlaylistId);
            await context.RespondAsync(Failure("internal_error", "Internal user playlist service error."));
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<UserPlaylistDeleteRequestMessage> context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.Message.OwnerSubject))
            {
                await context.RespondAsync(Failure("validation", "owner is required."));
                return;
            }

            var deleted = await WithRepo(repository => repository.DeleteAsync(context.Message.OwnerSubject, context.Message.PlaylistId));
            await context.RespondAsync(deleted
                ? new UserPlaylistResponseMessage { Success = true }
                : Failure("not_found", $"Playlist '{context.Message.PlaylistId}' was not found."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting user playlist {PlaylistId}.", context.Message.PlaylistId);
            await context.RespondAsync(Failure("internal_error", "Internal user playlist service error."));
        }
    }

    private async Task HandleGetAsync(IMessageContext<UserPlaylistGetRequestMessage> context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.Message.OwnerSubject))
            {
                await context.RespondAsync(Failure("validation", "owner is required."));
                return;
            }

            var detail = await WithRepo(repository => repository.GetAsync(context.Message.OwnerSubject, context.Message.PlaylistId));
            await context.RespondAsync(detail is null
                ? Failure("not_found", $"Playlist '{context.Message.PlaylistId}' was not found.")
                : await SuccessAsync(detail, context.Message.OwnerSubject));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting user playlist {PlaylistId}.", context.Message.PlaylistId);
            await context.RespondAsync(Failure("internal_error", "Internal user playlist service error."));
        }
    }

    private async Task HandleListAsync(IMessageContext<UserPlaylistListRequestMessage> context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.Message.OwnerSubject))
            {
                await context.RespondAsync(new UserPlaylistListResponseMessage
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = "owner is required."
                });
                return;
            }

            var items = await WithRepo(repository => repository.ListAsync(
                context.Message.OwnerSubject,
                context.Message.PageSize,
                context.Message.PageOffset));

            var mapped = await AttachNotesAsync(items.Select(MapSummary).ToArray(), context.Message.OwnerSubject);

            await context.RespondAsync(new UserPlaylistListResponseMessage
            {
                Success = true,
                Items = mapped
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing user playlists for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(new UserPlaylistListResponseMessage
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal user playlist service error."
            });
        }
    }

    private async Task HandleAddItemAsync(IMessageContext<UserPlaylistAddItemRequestMessage> context)
    {
        try
        {
            var result = await WithRepo(repository => repository.AddItemAsync(
                context.Message.OwnerSubject,
                context.Message.PlaylistId,
                context.Message.MediaGuid,
                context.Message.Position));
            await context.RespondAsync(await MapMutationAsync(result, context.Message.OwnerSubject));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed adding media {MediaGuid} to user playlist {PlaylistId}.", context.Message.MediaGuid, context.Message.PlaylistId);
            await context.RespondAsync(Failure("internal_error", "Internal user playlist service error."));
        }
    }

    private async Task HandleRemoveItemAsync(IMessageContext<UserPlaylistRemoveItemRequestMessage> context)
    {
        try
        {
            var result = await WithRepo(repository => repository.RemoveItemAsync(
                context.Message.OwnerSubject,
                context.Message.PlaylistId,
                context.Message.MediaGuid));
            await context.RespondAsync(await MapMutationAsync(result, context.Message.OwnerSubject));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed removing media {MediaGuid} from user playlist {PlaylistId}.", context.Message.MediaGuid, context.Message.PlaylistId);
            await context.RespondAsync(Failure("internal_error", "Internal user playlist service error."));
        }
    }

    private async Task HandleReorderItemsAsync(IMessageContext<UserPlaylistReorderItemsRequestMessage> context)
    {
        try
        {
            var result = await WithRepo(repository => repository.ReorderItemsAsync(
                context.Message.OwnerSubject,
                context.Message.PlaylistId,
                context.Message.MediaGuids));
            await context.RespondAsync(await MapMutationAsync(result, context.Message.OwnerSubject));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed reordering user playlist {PlaylistId}.", context.Message.PlaylistId);
            await context.RespondAsync(Failure("internal_error", "Internal user playlist service error."));
        }
    }

    private static (string Code, string Message)? ValidateOwnerAndName(string ownerSubject, string name)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject))
            return ("validation", "owner is required.");
        if (string.IsNullOrWhiteSpace(name))
            return ("validation", "name is required.");
        return null;
    }

    private Task<TResult> WithRepo<TResult>(Func<IUserPlaylistsRepository, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private async Task<UserPlaylistResponseMessage> MapMutationAsync(UserPlaylistMutationResult result, string ownerSubject)
        => result.Success && result.Detail is not null
            ? await SuccessAsync(result.Detail, ownerSubject)
            : Failure(result.ErrorCode ?? "unknown", result.ErrorMessage ?? "User playlist operation failed.");

    private static UserPlaylistResponseMessage Success(UserPlaylistDetail detail)
        => new() { Success = true, Playlist = MapDetail(detail) };

    private async Task<UserPlaylistResponseMessage> SuccessAsync(UserPlaylistDetail detail, string ownerSubject)
    {
        var playlist = await AttachNoteAsync(MapDetail(detail), ownerSubject);
        return new UserPlaylistResponseMessage { Success = true, Playlist = playlist };
    }

    private static UserPlaylistResponseMessage Failure((string Code, string Message) validation)
        => Failure(validation.Code, validation.Message);

    private static UserPlaylistResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static UserPlaylistDto MapSummary(UserPlaylistSummary summary)
        => new()
        {
            PlaylistId = summary.Playlist.PlaylistId,
            Name = summary.Playlist.Name,
            Description = summary.Playlist.Description,
            CreatedAt = summary.Playlist.CreatedAt,
            UpdatedAt = summary.Playlist.UpdatedAt,
            ItemCount = summary.ItemCount,
            Items = null
        };

    private static UserPlaylistDto MapDetail(UserPlaylistDetail detail)
        => new()
        {
            PlaylistId = detail.Playlist.PlaylistId,
            Name = detail.Playlist.Name,
            Description = detail.Playlist.Description,
            CreatedAt = detail.Playlist.CreatedAt,
            UpdatedAt = detail.Playlist.UpdatedAt,
            ItemCount = detail.Items.Count,
            Items = detail.Items
                .OrderBy(x => x.Position)
                .Select(x => new UserPlaylistItemDto
                {
                    MediaGuid = x.MediaGuid,
                    Position = x.Position,
                    AddedAt = x.AddedAt
                })
                .ToArray()
        };

    private async Task<UserPlaylistDto> AttachNoteAsync(UserPlaylistDto item, string ownerSubject)
    {
        var items = await AttachNotesAsync([item], ownerSubject);
        return items[0];
    }

    private async Task<IReadOnlyList<UserPlaylistDto>> AttachNotesAsync(
        IReadOnlyList<UserPlaylistDto> items,
        string ownerSubject)
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
}
