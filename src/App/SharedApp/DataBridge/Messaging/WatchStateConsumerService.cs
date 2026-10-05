using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging;
using NodaTime;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;
using static DataBridge.ApplicationDataReaderExtensions;

namespace DataBridge.Messaging;

public sealed class WatchStateConsumerService(
    IMessageBus messageBus,
    ApplicationDatabase dataSource,
    IClock clock,
    ILogger<WatchStateConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-watch-states";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<WatchStateUpsertRequest>(messageBus, WatchStateSubjects.Upsert, HandleUpsertAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<WatchStateGetRequest>(messageBus, WatchStateSubjects.Get, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<WatchStateInProgressListRequest>(messageBus, WatchStateSubjects.ListInProgress, HandleListInProgressAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<WatchStateHistoryListRequest>(messageBus, WatchStateSubjects.ListHistory, HandleListHistoryAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<MediaLikeStateRequest>(messageBus, MediaLikeSubjects.Get, HandleGetLikeAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<MediaLikeStateRequest>(messageBus, MediaLikeSubjects.Like, HandleLikeAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<MediaLikeStateRequest>(messageBus, MediaLikeSubjects.Unlike, HandleUnlikeAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<MediaLikeListRequest>(messageBus, MediaLikeSubjects.List, HandleListLikesAsync, QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to watch-state subjects.");
    }

    private async Task HandleUpsertAsync(IMessageContext<WatchStateUpsertRequest> context)
    {
        var request = context.Message;
        try
        {
            if (Validate(request) is { } validation)
            {
                await context.RespondAsync(Failure(validation));
                return;
            }

            if (!await MediaExistsAsync(request.MediaGuid, CancellationToken.None))
            {
                await context.RespondAsync(new WatchStateResponse
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Media '{request.MediaGuid}' was not found."
                });
                return;
            }

            var now = clock.GetCurrentInstant();
            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleUpsertAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("media_guid", request.MediaGuid);
            command.Parameters.AddWithValue("position_seconds", (object?)request.PositionSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("duration_seconds", (object?)request.DurationSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("completed", request.Completed);
            command.Parameters.AddWithValue("watched_at", request.Completed ? now.ToDateTimeOffset() : DBNull.Value);
            command.Parameters.AddWithValue("now", now.ToDateTimeOffset());

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                await context.RespondAsync(Failure("Failed to persist watch state."));
                return;
            }

            await context.RespondAsync(new WatchStateResponse
            {
                Success = true,
                State = Map(reader)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed upserting watch state for media {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new WatchStateResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to upsert watch state."
            });
        }
    }

    private async Task HandleGetAsync(IMessageContext<WatchStateGetRequest> context)
    {
        var request = context.Message;
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerSubject))
            {
                await context.RespondAsync(Failure("ownerSubject is required."));
                return;
            }

            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleGetAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("media_guid", request.MediaGuid);

            await using var reader = await command.ExecuteReaderAsync();
            await context.RespondAsync(new WatchStateResponse
            {
                Success = true,
                State = await reader.ReadAsync() ? Map(reader) : null
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting watch state for media {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new WatchStateResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to get watch state."
            });
        }
    }

    private async Task HandleListInProgressAsync(IMessageContext<WatchStateInProgressListRequest> context)
    {
        var request = context.Message;
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerSubject))
            {
                await context.RespondAsync(new WatchStateListResponse
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = "ownerSubject is required."
                });
                return;
            }

            var limit = Math.Clamp(request.Limit, 1, 100);
            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleListInProgressAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("limit", limit);

            var items = new List<WatchStateDto>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                items.Add(Map(reader));

            await context.RespondAsync(new WatchStateListResponse
            {
                Success = true,
                Items = items
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing in-progress watch states for {OwnerSubject}.", request.OwnerSubject);
            await context.RespondAsync(new WatchStateListResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to list in-progress watch states."
            });
        }
    }

    private async Task HandleListHistoryAsync(IMessageContext<WatchStateHistoryListRequest> context)
    {
        var request = context.Message;
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);

        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerSubject))
            {
                await context.RespondAsync(new WatchStateHistoryListResponse
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = "ownerSubject is required.",
                    Page = page
                });
                return;
            }

            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleListHistoryAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("limit", pageSize);
            command.Parameters.AddWithValue("offset", (page - 1) * pageSize);

            var items = new List<WatchHistoryItemDto>();
            var totalCount = 0;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (totalCount == 0)
                    totalCount = (int)Math.Min(int.MaxValue, reader.GetInt64(reader.GetOrdinal("total_count")));

                items.Add(new WatchHistoryItemDto
                {
                    WatchState = Map(reader),
                    Media = MapMetadataCard(reader)
                });
            }

            await context.RespondAsync(new WatchStateHistoryListResponse
            {
                Success = true,
                Items = items,
                Page = page,
                TotalCount = totalCount,
                HasMore = page * pageSize < totalCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing watch history for {OwnerSubject}.", request.OwnerSubject);
            await context.RespondAsync(new WatchStateHistoryListResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to list watch history.",
                Page = page
            });
        }
    }

    private async Task HandleGetLikeAsync(IMessageContext<MediaLikeStateRequest> context)
    {
        var request = context.Message;
        try
        {
            if (ValidateLikeRequest(request) is { } validation)
            {
                await context.RespondAsync(LikeFailure(validation));
                return;
            }

            if (!await MediaExistsAsync(request.MediaGuid, CancellationToken.None))
            {
                await context.RespondAsync(LikeNotFound(request.MediaGuid));
                return;
            }

            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleGetLikeAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("media_guid", request.MediaGuid);

            await using var reader = await command.ExecuteReaderAsync();
            await context.RespondAsync(new MediaLikeStateResponse
            {
                Success = true,
                State = await reader.ReadAsync()
                    ? MapLike(reader, liked: true)
                    : EmptyLikeState(request.OwnerSubject.Trim(), request.MediaGuid)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting like state for media {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new MediaLikeStateResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to get like state."
            });
        }
    }

    private async Task HandleLikeAsync(IMessageContext<MediaLikeStateRequest> context)
    {
        var request = context.Message;
        try
        {
            if (ValidateLikeRequest(request) is { } validation)
            {
                await context.RespondAsync(LikeFailure(validation));
                return;
            }

            if (!await MediaExistsAsync(request.MediaGuid, CancellationToken.None))
            {
                await context.RespondAsync(LikeNotFound(request.MediaGuid));
                return;
            }

            var now = clock.GetCurrentInstant();
            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleLikeAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("media_guid", request.MediaGuid);
            command.Parameters.AddWithValue("now", now.ToDateTimeOffset());

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                await context.RespondAsync(LikeFailure("Failed to persist like state."));
                return;
            }

            await context.RespondAsync(new MediaLikeStateResponse
            {
                Success = true,
                State = MapLike(reader, liked: true)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed liking media {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new MediaLikeStateResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to like media."
            });
        }
    }

    private async Task HandleUnlikeAsync(IMessageContext<MediaLikeStateRequest> context)
    {
        var request = context.Message;
        try
        {
            if (ValidateLikeRequest(request) is { } validation)
            {
                await context.RespondAsync(LikeFailure(validation));
                return;
            }

            if (!await MediaExistsAsync(request.MediaGuid, CancellationToken.None))
            {
                await context.RespondAsync(LikeNotFound(request.MediaGuid));
                return;
            }

            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleUnlikeAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("media_guid", request.MediaGuid);
            await command.ExecuteNonQueryAsync();

            await context.RespondAsync(new MediaLikeStateResponse
            {
                Success = true,
                State = EmptyLikeState(request.OwnerSubject.Trim(), request.MediaGuid)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed unliking media {MediaGuid}.", request.MediaGuid);
            await context.RespondAsync(new MediaLikeStateResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to unlike media."
            });
        }
    }

    private async Task HandleListLikesAsync(IMessageContext<MediaLikeListRequest> context)
    {
        var request = context.Message;
        var page = NormalizePage(request.Page);
        var pageSize = NormalizePageSize(request.PageSize);

        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerSubject))
            {
                await context.RespondAsync(new MediaLikeListResponse
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = "ownerSubject is required.",
                    Page = page
                });
                return;
            }

            await using var command = dataSource.CreateCommand(dataSource.Sql("WatchStateConsumerService.HandleListLikesAsync.1"));
            command.Parameters.AddWithValue("owner_subject", request.OwnerSubject.Trim());
            command.Parameters.AddWithValue("limit", pageSize);
            command.Parameters.AddWithValue("offset", (page - 1) * pageSize);

            var items = new List<LikedMediaItemDto>();
            var totalCount = 0;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (totalCount == 0)
                    totalCount = (int)Math.Min(int.MaxValue, reader.GetInt64(reader.GetOrdinal("total_count")));

                items.Add(new LikedMediaItemDto
                {
                    Like = MapLike(reader, liked: true),
                    Media = MapMetadataCard(reader)
                });
            }

            await context.RespondAsync(new MediaLikeListResponse
            {
                Success = true,
                Items = items,
                Page = page,
                TotalCount = totalCount,
                HasMore = page * pageSize < totalCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing liked media for {OwnerSubject}.", request.OwnerSubject);
            await context.RespondAsync(new MediaLikeListResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to list liked media.",
                Page = page
            });
        }
    }

    private async Task<bool> MediaExistsAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            dataSource.Sql("WatchStateConsumerService.MediaExistsAsync.1"));
        command.Parameters.AddWithValue("media_guid", mediaGuid);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static string? Validate(WatchStateUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerSubject))
            return "ownerSubject is required.";
        if (request.PositionSeconds is < 0)
            return "positionSeconds must be greater than or equal to zero.";
        if (request.DurationSeconds is <= 0)
            return "durationSeconds must be greater than zero.";
        return null;
    }

    private static WatchStateResponse Failure(string message)
        => new() { Success = false, ErrorCode = "validation", ErrorMessage = message };

    private static MediaLikeStateResponse LikeFailure(string message)
        => new() { Success = false, ErrorCode = "validation", ErrorMessage = message };

    private static MediaLikeStateResponse LikeNotFound(Guid mediaGuid)
        => new()
        {
            Success = false,
            ErrorCode = "not_found",
            ErrorMessage = $"Media '{mediaGuid}' was not found."
        };

    private static string? ValidateLikeRequest(MediaLikeStateRequest request)
        => string.IsNullOrWhiteSpace(request.OwnerSubject) ? "ownerSubject is required." : null;

    private static int NormalizePage(int page)
        => Math.Max(1, page);

    private static int NormalizePageSize(int pageSize)
        => Math.Clamp(pageSize <= 0 ? 24 : pageSize, 1, 100);

    private static WatchStateDto Map(DbDataReader reader)
        => new()
        {
            OwnerSubject = reader.GetString(0),
            MediaGuid = reader.GetGuid(1),
            PositionSeconds = reader.IsDBNull(2) ? null : reader.GetDouble(2),
            DurationSeconds = reader.IsDBNull(3) ? null : reader.GetDouble(3),
            Completed = reader.GetBoolean(4),
            WatchedAt = reader.IsDBNull(5) ? null : Instant.FromDateTimeOffset(reader.GetApplicationValue<DateTimeOffset>(5)),
            LastPlayedAt = Instant.FromDateTimeOffset(reader.GetApplicationValue<DateTimeOffset>(6)),
            UpdatedAt = Instant.FromDateTimeOffset(reader.GetApplicationValue<DateTimeOffset>(7))
        };

    private static MediaLikeStateDto MapLike(DbDataReader reader, bool liked)
        => new()
        {
            OwnerSubject = GetString(reader, "owner_subject"),
            MediaGuid = GetGuid(reader, "media_guid"),
            Liked = liked,
            LikedAt = liked ? Instant.FromDateTimeOffset(reader.GetApplicationValue<DateTimeOffset>(reader.GetOrdinal("liked_at"))) : null,
            UpdatedAt = liked ? Instant.FromDateTimeOffset(reader.GetApplicationValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))) : null
        };

    private static MediaLikeStateDto EmptyLikeState(string ownerSubject, Guid mediaGuid)
        => new()
        {
            OwnerSubject = ownerSubject,
            MediaGuid = mediaGuid,
            Liked = false,
            LikedAt = null,
            UpdatedAt = null
        };

    private static MetadataCardDto MapMetadataCard(DbDataReader reader)
        => new()
        {
            MediaGuid = GetGuid(reader, "media_guid"),
            Title = GetString(reader, "title"),
            ThumbnailStoragePath = GetNullableString(reader, "thumbnail_storage_path"),
            DurationSeconds = GetNullableDouble(reader, "duration"),
            ReleaseDate = GetNullableInstant(reader, "release_date"),
            ViewCount = GetNullableInt64(reader, "view_count"),
            Availability = GetNullableString(reader, "availability"),
            WasLive = GetBoolean(reader, "was_live"),
            Account = new MetadataAccountCardDto
            {
                AccountId = GetInt64(reader, "account_id"),
                Platform = GetString(reader, "platform"),
                AccountName = GetString(reader, "account_name"),
                AccountHandle = GetString(reader, "account_handle"),
                AvatarStoragePath = GetNullableString(reader, "avatar_storage_path")
            }
        };
}
