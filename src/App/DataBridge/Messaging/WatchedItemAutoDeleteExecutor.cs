using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class WatchedItemAutoDeleteExecutor(
    NpgsqlDataSource dataSource,
    MediaDeleteExecutor mediaDeleteExecutor,
    IClock clock,
    ILogger<WatchedItemAutoDeleteExecutor> logger)
{
    private const int MaxAllowedRetentionDays = 3650;
    private const int MaxAllowedDeletesPerRun = 10_000;

    public async Task<WatchedAutoDeletePolicyResponse> GetPolicyAsync(CancellationToken cancellationToken = default)
        => new()
        {
            Success = true,
            Policy = await LoadPolicyAsync(cancellationToken)
        };

    public async Task<WatchedAutoDeletePolicyResponse> UpdatePolicyAsync(
        WatchedAutoDeletePolicyUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.DeleteAfterDays is < 1 or > MaxAllowedRetentionDays)
        {
            return FailurePolicy("validation", $"deleteAfterDays must be between 1 and {MaxAllowedRetentionDays}.");
        }

        if (request.MaxDeletionsPerRun is < 1 or > MaxAllowedDeletesPerRun)
        {
            return FailurePolicy("validation", $"maxDeletionsPerRun must be between 1 and {MaxAllowedDeletesPerRun}.");
        }

        var now = clock.GetCurrentInstant();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO maintenance.watched_item_auto_delete_policy
                (id, enabled, delete_after_days, max_deletions_per_run, updated_by, updated_at)
            VALUES
                (1, @enabled, @delete_after_days, @max_deletions_per_run, @updated_by, @updated_at)
            ON CONFLICT (id)
            DO UPDATE SET
                enabled = EXCLUDED.enabled,
                delete_after_days = EXCLUDED.delete_after_days,
                max_deletions_per_run = EXCLUDED.max_deletions_per_run,
                updated_by = EXCLUDED.updated_by,
                updated_at = EXCLUDED.updated_at;
            """);
        command.Parameters.AddWithValue("enabled", request.Enabled);
        command.Parameters.AddWithValue("delete_after_days", request.DeleteAfterDays);
        command.Parameters.AddWithValue("max_deletions_per_run", request.MaxDeletionsPerRun);
        command.Parameters.AddWithValue("updated_by", (object?)request.UpdatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", now.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new WatchedAutoDeletePolicyResponse
        {
            Success = true,
            Policy = await LoadPolicyAsync(cancellationToken)
        };
    }

    public async Task<WatchedAutoDeleteCleanupResponse> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var policy = await LoadPolicyAsync(cancellationToken);
        if (!policy.Enabled)
        {
            var disabledResult = new WatchedAutoDeleteCleanupResultDto
            {
                PolicyEnabled = false,
                Cutoff = null,
                CandidatesFound = 0,
                DeletedCount = 0,
                FailedCount = 0,
                FilesDeleted = 0
            };
            await RecordRunAsync(disabledResult, cancellationToken);
            return new WatchedAutoDeleteCleanupResponse { Success = true, Result = disabledResult };
        }

        var now = clock.GetCurrentInstant();
        var cutoff = now.Minus(Duration.FromDays(policy.DeleteAfterDays));
        var candidates = await LoadCandidatesAsync(cutoff, policy.MaxDeletionsPerRun, cancellationToken);

        var deleted = 0;
        var failed = 0;
        var filesDeleted = 0;
        foreach (var mediaGuid in candidates)
        {
            var response = await mediaDeleteExecutor.DeleteMediaAsync(mediaGuid, cancellationToken);
            if (response.Success && response.MediaRemoved)
            {
                deleted++;
                filesDeleted += response.FilesDeleted;
                continue;
            }

            failed++;
            logger.LogWarning(
                "Watched auto-delete skipped media {MediaGuid}: {ErrorCode} {ErrorMessage}",
                mediaGuid,
                response.ErrorCode,
                response.ErrorMessage);
        }

        var result = new WatchedAutoDeleteCleanupResultDto
        {
            PolicyEnabled = true,
            Cutoff = cutoff,
            CandidatesFound = candidates.Count,
            DeletedCount = deleted,
            FailedCount = failed,
            FilesDeleted = filesDeleted
        };
        await RecordRunAsync(result, cancellationToken);
        return new WatchedAutoDeleteCleanupResponse { Success = true, Result = result };
    }

    private async Task<IReadOnlyList<Guid>> LoadCandidatesAsync(
        Instant cutoff,
        int maxItems,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT ws.media_guid
            FROM media.watch_states ws
            JOIN media.media m ON m.media_guid = ws.media_guid
            WHERE ws.watched_at IS NOT NULL
            GROUP BY ws.media_guid
            HAVING max(ws.watched_at) <= @cutoff
            ORDER BY max(ws.watched_at), ws.media_guid
            LIMIT @max_items;
            """);
        command.Parameters.AddWithValue("cutoff", cutoff.ToDateTimeOffset());
        command.Parameters.AddWithValue("max_items", maxItems);

        var items = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(reader.GetGuid(0));
        }

        return items;
    }

    private async Task RecordRunAsync(WatchedAutoDeleteCleanupResultDto result, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE maintenance.watched_item_auto_delete_policy
            SET
                last_run_at = @last_run_at,
                last_deleted_count = @last_deleted_count,
                last_failed_count = @last_failed_count
            WHERE id = 1;
            """);
        command.Parameters.AddWithValue("last_run_at", clock.GetCurrentInstant().ToDateTimeOffset());
        command.Parameters.AddWithValue("last_deleted_count", result.DeletedCount);
        command.Parameters.AddWithValue("last_failed_count", result.FailedCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<WatchedAutoDeletePolicyDto> LoadPolicyAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT enabled, delete_after_days, max_deletions_per_run, updated_by, updated_at, last_run_at, last_deleted_count, last_failed_count
            FROM maintenance.watched_item_auto_delete_policy
            WHERE id = 1;
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new WatchedAutoDeletePolicyDto
            {
                Enabled = false,
                DeleteAfterDays = 30,
                MaxDeletionsPerRun = 100
            };
        }

        return new WatchedAutoDeletePolicyDto
        {
            Enabled = reader.GetBoolean(0),
            DeleteAfterDays = reader.GetInt32(1),
            MaxDeletionsPerRun = reader.GetInt32(2),
            UpdatedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
            UpdatedAt = reader.IsDBNull(4) ? null : Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(4)),
            LastRunAt = reader.IsDBNull(5) ? null : Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(5)),
            LastDeletedCount = reader.GetInt32(6),
            LastFailedCount = reader.GetInt32(7)
        };
    }

    private static WatchedAutoDeletePolicyResponse FailurePolicy(string errorCode, string errorMessage)
        => new()
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
}

