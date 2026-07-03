using Conduit.NATS;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class WatchStateConsumerService(
    IMessageBus messageBus,
    NpgsqlDataSource dataSource,
    IClock clock,
    ILogger<WatchStateConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-watch-states";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<WatchStateUpsertRequest>(messageBus, WatchStateSubjects.Upsert, HandleUpsertAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<WatchStateGetRequest>(messageBus, WatchStateSubjects.Get, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<WatchStateInProgressListRequest>(messageBus, WatchStateSubjects.ListInProgress, HandleListInProgressAsync, QueueGroup, stoppingToken);

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
            await using var command = dataSource.CreateCommand("""
                INSERT INTO media.watch_states
                    (owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, created_at, updated_at)
                VALUES
                    (@owner_subject, @media_guid, @position_seconds, @duration_seconds, @completed, @watched_at, @now, @now, @now)
                ON CONFLICT (owner_subject, media_guid)
                DO UPDATE SET
                    position_seconds = EXCLUDED.position_seconds,
                    duration_seconds = EXCLUDED.duration_seconds,
                    completed = EXCLUDED.completed,
                    watched_at = CASE
                        WHEN EXCLUDED.completed AND media.watch_states.completed THEN media.watch_states.watched_at
                        WHEN EXCLUDED.completed THEN EXCLUDED.watched_at
                        ELSE NULL
                    END,
                    last_played_at = EXCLUDED.last_played_at,
                    updated_at = EXCLUDED.updated_at
                RETURNING owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, updated_at;
                """);
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

            await using var command = dataSource.CreateCommand("""
                SELECT owner_subject, media_guid, position_seconds, duration_seconds, completed, watched_at, last_played_at, updated_at
                FROM media.watch_states
                WHERE owner_subject = @owner_subject AND media_guid = @media_guid;
                """);
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
            await using var command = dataSource.CreateCommand("""
                SELECT ws.owner_subject, ws.media_guid, ws.position_seconds, ws.duration_seconds,
                       ws.completed, ws.watched_at, ws.last_played_at, ws.updated_at
                FROM media.watch_states ws
                JOIN media.media m ON m.media_guid = ws.media_guid
                WHERE ws.owner_subject = @owner_subject
                  AND NOT ws.completed
                  AND ws.position_seconds IS NOT NULL
                  AND ws.position_seconds > 0
                ORDER BY ws.last_played_at DESC
                LIMIT @limit;
                """);
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

    private async Task<bool> MediaExistsAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM media.media WHERE media_guid = @media_guid);");
        command.Parameters.AddWithValue("media_guid", mediaGuid);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
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

    private static WatchStateDto Map(NpgsqlDataReader reader)
        => new()
        {
            OwnerSubject = reader.GetString(0),
            MediaGuid = reader.GetGuid(1),
            PositionSeconds = reader.IsDBNull(2) ? null : reader.GetDouble(2),
            DurationSeconds = reader.IsDBNull(3) ? null : reader.GetDouble(3),
            Completed = reader.GetBoolean(4),
            WatchedAt = reader.IsDBNull(5) ? null : Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(5)),
            LastPlayedAt = Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(6)),
            UpdatedAt = Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(7))
        };
}
