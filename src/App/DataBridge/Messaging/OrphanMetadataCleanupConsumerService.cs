using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class OrphanMetadataCleanupExecutor
{
    private const int RetentionDays = 30;
    private const int MaxPageSize = 500;
    private static readonly TimeSpan WorkerRequestTimeout = TimeSpan.FromMinutes(5);
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMessageBus _messageBus;

    public OrphanMetadataCleanupExecutor(NpgsqlDataSource dataSource, IMessageBus messageBus)
    {
        _dataSource = dataSource;
        _messageBus = messageBus;
    }

    public OrphanMetadataCleanupExecutor(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        _messageBus = new UnavailableMessageBus();
    }

    public async Task<OrphanMetadataCleanupResult> CleanupAsync(
        Instant now,
        CancellationToken cancellationToken = default)
    {
        var detected = await RecordCurrentFindingsAsync(now, cancellationToken);
        var resolvedCount = await ResolveDisappearedFindingsAsync(now, cancellationToken);
        var moveResult = await MoveDetectedFileOrphansAsync(now, cancellationToken);
        var deleteResult = await DeleteExpiredFileOrphansAsync(now, cancellationToken);
        var deletedMediaCount = await FinalizeExpiredMetadataOrphansAsync(now, cancellationToken);

        return new OrphanMetadataCleanupResult(
            detected.MetadataWithoutMediaCount,
            detected.MediaWithoutMetadataCount,
            resolvedCount,
            moveResult.Succeeded,
            moveResult.Failed,
            deleteResult.Succeeded,
            deleteResult.Failed,
            deletedMediaCount);
    }

    public async Task<OrphanCleanupListResponse> ListAsync(
        OrphanCleanupListRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.PageSize is < 1 or > MaxPageSize)
        {
            return FailureList("validation", $"Page size must be between 1 and {MaxPageSize}.");
        }

        if (request.Page < 1)
        {
            return FailureList("validation", "Page must be greater than zero.");
        }

        var offset = (request.Page - 1) * request.PageSize;
        var items = new List<OrphanCleanupItemDto>();
        await using var command = _dataSource.CreateCommand("""
            SELECT
                id,
                item_kind,
                state,
                storage_key,
                original_storage_path,
                orphan_storage_path,
                media_guid,
                detected_at,
                last_seen_at,
                delete_after,
                moved_at,
                finalized_at,
                resolved_at,
                last_error,
                created_at,
                updated_at
            FROM orphan_cleanup_items
            WHERE (@kind IS NULL OR item_kind = @kind)
              AND (@state IS NULL OR state = @state)
            ORDER BY detected_at DESC, id DESC
            LIMIT @limit
            OFFSET @offset;
            """);
        command.Parameters.Add("kind", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(request.Kind) ? DBNull.Value : request.Kind;
        command.Parameters.Add("state", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(request.State) ? DBNull.Value : request.State;
        command.Parameters.AddWithValue("limit", request.PageSize);
        command.Parameters.AddWithValue("offset", offset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new OrphanCleanupItemDto
            {
                Id = reader.GetInt64(0),
                Kind = reader.GetString(1),
                State = reader.GetString(2),
                StorageKey = reader.GetString(3),
                OriginalStoragePath = reader.GetString(4),
                OrphanStoragePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                MediaGuid = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                DetectedAt = reader.GetFieldValue<DateTimeOffset>(7),
                LastSeenAt = reader.GetFieldValue<DateTimeOffset>(8),
                DeleteAfter = reader.GetFieldValue<DateTimeOffset>(9),
                MovedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                FinalizedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                ResolvedAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                LastError = reader.IsDBNull(13) ? null : reader.GetString(13),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(14),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(15)
            });
        }

        return new OrphanCleanupListResponse { Success = true, Items = items };
    }

    public async Task<RestoreOrphanResponse> RestoreFileOrphanAsync(
        long orphanId,
        Instant now,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadOrphanItemAsync(orphanId, cancellationToken);
        if (item is null)
        {
            return FailureRestore("not_found", $"Orphan cleanup item '{orphanId}' was not found.");
        }

        var validationError = ValidateFileRestoreItem(item);
        if (validationError is not null)
        {
            return validationError;
        }

        try
        {
            var response = await _messageBus.RequestAsync<RestoreOrphanedFileRequest, RestoreOrphanedFileResponse>(
                OrphanCleanupSubjects.RestoreFile,
                new RestoreOrphanedFileRequest
                {
                    OrphanId = item.Id,
                    StorageKey = item.StorageKey,
                    OrphanStoragePath = item.OrphanStoragePath!,
                    OriginalStoragePath = item.OriginalStoragePath
                },
                WorkerRequestTimeout,
                cancellationToken);

            if (response is not { Success: true })
            {
                return FailureRestore(response?.ErrorCode ?? "unavailable", response?.ErrorMessage ?? "Worker did not respond.");
            }

            await MarkFileOrphanRestoredAsync(item, now, cancellationToken);
            return new RestoreOrphanResponse { Success = true };
        }
        catch (Exception ex)
        {
            return FailureRestore("unavailable", ex.Message);
        }
    }

    public async Task<RestoreOrphanResponse> RestoreMetadataOrphanAsync(
        long orphanId,
        Instant now,
        CancellationToken cancellationToken = default)
    {
        var item = await LoadOrphanItemAsync(orphanId, cancellationToken);
        if (item is null)
        {
            return FailureRestore("not_found", $"Orphan cleanup item '{orphanId}' was not found.");
        }

        var validationError = ValidateMetadataRestoreItem(item);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!await ContentVersionExistsAsync(item, cancellationToken))
        {
            return FailureRestore("conflict", "Expected media content version row is missing.");
        }

        try
        {
            var existsResponse = await _messageBus.RequestAsync<OrphanFileExistsRequest, OrphanFileExistsResponse>(
                OrphanCleanupSubjects.FileExists,
                new OrphanFileExistsRequest
                {
                    StorageKey = item.StorageKey,
                    StoragePath = item.OriginalStoragePath
                },
                WorkerRequestTimeout,
                cancellationToken);

            if (existsResponse is not { Success: true })
            {
                return FailureRestore(existsResponse?.ErrorCode ?? "unavailable", existsResponse?.ErrorMessage ?? "Worker did not respond.");
            }

            if (!existsResponse.Exists)
            {
                return FailureRestore("conflict", "Expected file is still missing from storage.");
            }

            await MarkMetadataOrphanRestoredAsync(item, now, cancellationToken);
            return new RestoreOrphanResponse { Success = true };
        }
        catch (Exception ex)
        {
            return FailureRestore("unavailable", ex.Message);
        }
    }

    private async Task<(long MetadataWithoutMediaCount, long MediaWithoutMetadataCount)> RecordCurrentFindingsAsync(
        Instant now,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            WITH metadata_without_media AS (
                INSERT INTO orphan_cleanup_items
                    (item_kind, state, storage_key, original_storage_path, media_guid, detected_at, last_seen_at, delete_after, created_at, updated_at)
                SELECT
                    'metadata_without_media',
                    'detected',
                    finding.storage_key,
                    finding.storage_path,
                    finding.media_guid,
                    finding.detected_at,
                    finding.last_seen_at,
                    finding.detected_at + (@retention_days * INTERVAL '1 day'),
                    @now,
                    @now
                FROM filesystem_rescan_findings finding
                JOIN media_content_id_versions civ
                  ON civ.media_guid = finding.media_guid
                 AND civ.storage_key = finding.storage_key
                 AND civ.storage_path = finding.storage_path
                WHERE finding.finding_type = 'MissingFile'
                  AND finding.resolved_at IS NULL
                  AND finding.media_guid IS NOT NULL
                ON CONFLICT (item_kind, storage_key, original_storage_path, (COALESCE(media_guid, '00000000-0000-0000-0000-000000000000'::uuid)))
                DO UPDATE SET
                    state = CASE
                        WHEN orphan_cleanup_items.state IN ('resolved', 'finalized') THEN 'detected'
                        ELSE orphan_cleanup_items.state
                    END,
                    last_seen_at = EXCLUDED.last_seen_at,
                    resolved_at = NULL,
                    last_error = CASE
                        WHEN orphan_cleanup_items.state = 'resolved' THEN NULL
                        ELSE orphan_cleanup_items.last_error
                    END,
                    updated_at = @now
                RETURNING 1
            ),
            media_without_metadata AS (
                INSERT INTO orphan_cleanup_items
                    (item_kind, state, storage_key, original_storage_path, detected_at, last_seen_at, delete_after, created_at, updated_at)
                SELECT
                    'media_without_metadata',
                    'detected',
                    finding.storage_key,
                    finding.storage_path,
                    finding.detected_at,
                    finding.last_seen_at,
                    finding.detected_at + (@retention_days * INTERVAL '1 day'),
                    @now,
                    @now
                FROM filesystem_rescan_findings finding
                WHERE finding.finding_type = 'UnexpectedFile'
                  AND finding.resolved_at IS NULL
                  AND lower(finding.storage_path) NOT LIKE 'orphaned/%'
                  AND lower(finding.storage_path) ~ '\.(mp4|mkv|webm|mov|avi|m4v|mp3|m4a|flac|wav|ogg|opus)$'
                ON CONFLICT (item_kind, storage_key, original_storage_path, (COALESCE(media_guid, '00000000-0000-0000-0000-000000000000'::uuid)))
                DO UPDATE SET
                    state = CASE
                        WHEN orphan_cleanup_items.state IN ('resolved', 'finalized') THEN 'detected'
                        ELSE orphan_cleanup_items.state
                    END,
                    last_seen_at = EXCLUDED.last_seen_at,
                    resolved_at = NULL,
                    last_error = CASE
                        WHEN orphan_cleanup_items.state = 'resolved' THEN NULL
                        ELSE orphan_cleanup_items.last_error
                    END,
                    updated_at = @now
                RETURNING 1
            )
            SELECT
                (SELECT count(*)::bigint FROM metadata_without_media),
                (SELECT count(*)::bigint FROM media_without_metadata);
            """);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        command.Parameters.AddWithValue("retention_days", RetentionDays);
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<long> ResolveDisappearedFindingsAsync(Instant now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            WITH resolved AS (
                UPDATE orphan_cleanup_items item
                SET
                    state = 'resolved',
                    resolved_at = @now,
                    updated_at = @now,
                    last_error = NULL
                WHERE item.state IN ('detected', 'move_failed', 'delete_failed')
                  AND (
                      (
                          item.item_kind = 'metadata_without_media'
                          AND NOT EXISTS (
                              SELECT 1
                              FROM filesystem_rescan_findings finding
                              WHERE finding.finding_type = 'MissingFile'
                                AND finding.resolved_at IS NULL
                                AND finding.media_guid = item.media_guid
                                AND finding.storage_key = item.storage_key
                                AND finding.storage_path = item.original_storage_path
                          )
                      )
                      OR
                      (
                          item.item_kind = 'media_without_metadata'
                          AND item.orphan_storage_path IS NULL
                          AND NOT EXISTS (
                              SELECT 1
                              FROM filesystem_rescan_findings finding
                              WHERE finding.finding_type = 'UnexpectedFile'
                                AND finding.resolved_at IS NULL
                                AND finding.storage_key = item.storage_key
                                AND finding.storage_path = item.original_storage_path
                          )
                      )
                  )
                RETURNING 1
            )
            SELECT count(*)::bigint FROM resolved;
            """);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        command.CommandTimeout = 0;

        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private async Task<(long Succeeded, long Failed)> MoveDetectedFileOrphansAsync(
        Instant now,
        CancellationToken cancellationToken)
    {
        var candidates = await LoadMoveCandidatesAsync(cancellationToken);
        long succeeded = 0;
        long failed = 0;

        foreach (var candidate in candidates)
        {
            var orphanPath = BuildOrphanStoragePath(candidate);
            try
            {
                var response = await _messageBus.RequestAsync<MoveOrphanedFileRequest, MoveOrphanedFileResponse>(
                    OrphanCleanupSubjects.MoveFile,
                    new MoveOrphanedFileRequest
                    {
                        OrphanId = candidate.Id,
                        StorageKey = candidate.StorageKey,
                        OriginalStoragePath = candidate.OriginalStoragePath,
                        OrphanStoragePath = orphanPath
                    },
                    WorkerRequestTimeout,
                    cancellationToken);

                if (response is { Success: true })
                {
                    await MarkMovedAsync(candidate.Id, orphanPath, now, cancellationToken);
                    succeeded++;
                    continue;
                }

                await MarkMoveFailedAsync(candidate.Id, orphanPath, response?.ErrorMessage ?? "Worker did not respond.", now, cancellationToken);
                failed++;
            }
            catch (Exception ex)
            {
                await MarkMoveFailedAsync(candidate.Id, orphanPath, ex.Message, now, cancellationToken);
                failed++;
            }
        }

        return (succeeded, failed);
    }

    private async Task<(long Succeeded, long Failed)> DeleteExpiredFileOrphansAsync(
        Instant now,
        CancellationToken cancellationToken)
    {
        var candidates = await LoadDeleteFileCandidatesAsync(now, cancellationToken);
        long succeeded = 0;
        long failed = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                var response = await _messageBus.RequestAsync<DeleteOrphanedFileRequest, DeleteOrphanedFileResponse>(
                    OrphanCleanupSubjects.DeleteFile,
                    new DeleteOrphanedFileRequest
                    {
                        OrphanId = candidate.Id,
                        StorageKey = candidate.StorageKey,
                        OrphanStoragePath = candidate.OrphanStoragePath
                    },
                    WorkerRequestTimeout,
                    cancellationToken);

                if (response is { Success: true })
                {
                    await MarkFinalizedAsync(candidate.Id, now, cancellationToken);
                    succeeded++;
                    continue;
                }

                await MarkDeleteFailedAsync(candidate.Id, response?.ErrorMessage ?? "Worker did not respond.", now, cancellationToken);
                failed++;
            }
            catch (Exception ex)
            {
                await MarkDeleteFailedAsync(candidate.Id, ex.Message, now, cancellationToken);
                failed++;
            }
        }

        return (succeeded, failed);
    }

    private async Task<long> FinalizeExpiredMetadataOrphansAsync(Instant now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            WITH due_media AS (
                SELECT DISTINCT media_guid
                FROM orphan_cleanup_items
                WHERE item_kind = 'metadata_without_media'
                  AND state IN ('detected', 'delete_failed')
                  AND delete_after <= @now
                  AND media_guid IS NOT NULL
            ),
            candidates AS (
                SELECT m.media_guid
                FROM media m
                JOIN due_media due ON due.media_guid = m.media_guid
                WHERE EXISTS (
                    SELECT 1
                    FROM media_content_id_versions civ
                    WHERE civ.media_guid = m.media_guid
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM media_content_id_versions civ
                    WHERE civ.media_guid = m.media_guid
                    AND NOT EXISTS (
                        SELECT 1
                        FROM filesystem_rescan_findings finding
                        WHERE finding.media_guid = civ.media_guid
                          AND finding.storage_key = civ.storage_key
                          AND finding.storage_path = civ.storage_path
                          AND finding.finding_type = 'MissingFile'
                          AND finding.resolved_at IS NULL
                          AND finding.detected_at + (@retention_days * INTERVAL '1 day') <= @now
                    )
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM media_source_versions sv
                    JOIN download_jobs dj ON dj.job_id = sv.latest_job_id
                    WHERE sv.media_guid = m.media_guid
                    AND dj.state IN (
                        'queued',
                        'metadata_pending',
                        'metadata_resolved',
                        'download_pending',
                        'downloaded_temp',
                        'upload_pending',
                        'uploaded',
                        'commit_pending',
                        'compensating',
                        'failed_transient'
                    )
                )
            ),
            candidate_count AS (
                SELECT count(*)::bigint AS count FROM candidates
            ),
            deleted AS (
                DELETE FROM media m
                USING candidates c
                WHERE m.media_guid = c.media_guid
                RETURNING m.media_guid
            ),
            finalized AS (
                UPDATE orphan_cleanup_items item
                SET
                    state = 'finalized',
                    finalized_at = @now,
                    updated_at = @now,
                    last_error = NULL
                FROM deleted
                WHERE item.item_kind = 'metadata_without_media'
                  AND item.media_guid = deleted.media_guid
                  AND item.state IN ('detected', 'delete_failed')
                RETURNING 1
            )
            SELECT count(*)::bigint FROM deleted;
            """);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        command.Parameters.AddWithValue("retention_days", RetentionDays);
        command.CommandTimeout = 0;

        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private async Task<IReadOnlyList<MoveCandidate>> LoadMoveCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<MoveCandidate>();
        await using var command = _dataSource.CreateCommand("""
            SELECT id, storage_key, original_storage_path, detected_at
            FROM orphan_cleanup_items
            WHERE item_kind = 'media_without_metadata'
              AND state IN ('detected', 'move_failed')
              AND resolved_at IS NULL
              AND finalized_at IS NULL
            ORDER BY detected_at, id
            LIMIT 100;
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new MoveCandidate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(3))));
        }

        return candidates;
    }

    private async Task<IReadOnlyList<DeleteFileCandidate>> LoadDeleteFileCandidatesAsync(Instant now, CancellationToken cancellationToken)
    {
        var candidates = new List<DeleteFileCandidate>();
        await using var command = _dataSource.CreateCommand("""
            SELECT id, storage_key, orphan_storage_path
            FROM orphan_cleanup_items
            WHERE item_kind = 'media_without_metadata'
              AND state IN ('moved', 'delete_failed')
              AND delete_after <= @now
              AND orphan_storage_path IS NOT NULL
              AND finalized_at IS NULL
            ORDER BY delete_after, id
            LIMIT 100;
            """);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new DeleteFileCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return candidates;
    }

    private async Task MarkMovedAsync(long id, string orphanStoragePath, Instant now, CancellationToken cancellationToken)
        => await UpdateItemAsync(
            id,
            "moved",
            now,
            cancellationToken,
            orphanStoragePath: orphanStoragePath,
            movedAt: now,
            error: null);

    private async Task MarkMoveFailedAsync(long id, string orphanStoragePath, string error, Instant now, CancellationToken cancellationToken)
        => await UpdateItemAsync(id, "move_failed", now, cancellationToken, orphanStoragePath: orphanStoragePath, error: error);

    private async Task MarkFinalizedAsync(long id, Instant now, CancellationToken cancellationToken)
        => await UpdateItemAsync(id, "finalized", now, cancellationToken, finalizedAt: now, error: null);

    private async Task MarkDeleteFailedAsync(long id, string error, Instant now, CancellationToken cancellationToken)
        => await UpdateItemAsync(id, "delete_failed", now, cancellationToken, error: error);

    private async Task MarkFileOrphanRestoredAsync(OrphanCleanupItem item, Instant now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            WITH restored AS (
                UPDATE orphan_cleanup_items
                SET
                    state = 'resolved',
                    resolved_at = @now,
                    last_error = NULL,
                    updated_at = @now
                WHERE id = @id
                  AND item_kind = 'media_without_metadata'
                  AND state IN ('moved', 'delete_failed')
                  AND finalized_at IS NULL
                  AND orphan_storage_path IS NOT NULL
                RETURNING storage_key, original_storage_path
            )
            UPDATE filesystem_rescan_findings finding
            SET resolved_at = @now
            FROM restored
            WHERE finding.finding_type = 'UnexpectedFile'
              AND finding.resolved_at IS NULL
              AND finding.storage_key = restored.storage_key
              AND finding.storage_path = restored.original_storage_path;
            """);
        command.Parameters.AddWithValue("id", item.Id);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkMetadataOrphanRestoredAsync(OrphanCleanupItem item, Instant now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            WITH restored AS (
                UPDATE orphan_cleanup_items
                SET
                    state = 'resolved',
                    resolved_at = @now,
                    last_error = NULL,
                    updated_at = @now
                WHERE id = @id
                  AND item_kind = 'metadata_without_media'
                  AND state IN ('detected', 'delete_failed')
                  AND finalized_at IS NULL
                  AND media_guid IS NOT NULL
                RETURNING storage_key, original_storage_path, media_guid
            )
            UPDATE filesystem_rescan_findings finding
            SET resolved_at = @now
            FROM restored
            WHERE finding.finding_type = 'MissingFile'
              AND finding.resolved_at IS NULL
              AND finding.media_guid = restored.media_guid
              AND finding.storage_key = restored.storage_key
              AND finding.storage_path = restored.original_storage_path;
            """);
        command.Parameters.AddWithValue("id", item.Id);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<OrphanCleanupItem?> LoadOrphanItemAsync(long orphanId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT
                id,
                item_kind,
                state,
                storage_key,
                original_storage_path,
                orphan_storage_path,
                media_guid,
                finalized_at,
                resolved_at
            FROM orphan_cleanup_items
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", orphanId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OrphanCleanupItem(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
    }

    private async Task<bool> ContentVersionExistsAsync(OrphanCleanupItem item, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM media_content_id_versions
                WHERE media_guid = @media_guid
                  AND storage_key = @storage_key
                  AND storage_path = @storage_path
            );
            """);
        command.Parameters.AddWithValue("media_guid", item.MediaGuid!.Value);
        command.Parameters.AddWithValue("storage_key", item.StorageKey);
        command.Parameters.AddWithValue("storage_path", item.OriginalStoragePath);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task UpdateItemAsync(
        long id,
        string state,
        Instant now,
        CancellationToken cancellationToken,
        string? orphanStoragePath = null,
        Instant? movedAt = null,
        Instant? finalizedAt = null,
        string? error = null)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE orphan_cleanup_items
            SET
                state = @state,
                orphan_storage_path = COALESCE(@orphan_storage_path, orphan_storage_path),
                moved_at = COALESCE(@moved_at, moved_at),
                finalized_at = COALESCE(@finalized_at, finalized_at),
                last_error = @last_error,
                updated_at = @now
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.Add("orphan_storage_path", NpgsqlDbType.Text).Value = (object?)orphanStoragePath ?? DBNull.Value;
        command.Parameters.Add("moved_at", NpgsqlDbType.TimestampTz).Value = (object?)movedAt?.ToDateTimeOffset() ?? DBNull.Value;
        command.Parameters.Add("finalized_at", NpgsqlDbType.TimestampTz).Value = (object?)finalizedAt?.ToDateTimeOffset() ?? DBNull.Value;
        command.Parameters.Add("last_error", NpgsqlDbType.Text).Value = (object?)error ?? DBNull.Value;
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildOrphanStoragePath(MoveCandidate candidate)
    {
        var normalizedOriginal = candidate.OriginalStoragePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalizedOriginal);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "file";
        }

        return FormattableString.Invariant(
            $"orphaned/{candidate.DetectedAt.ToDateTimeUtc():yyyyMMdd}/{candidate.Id}/{fileName}");
    }

    private sealed record MoveCandidate(long Id, string StorageKey, string OriginalStoragePath, Instant DetectedAt);

    private sealed record DeleteFileCandidate(long Id, string StorageKey, string OrphanStoragePath);

    private sealed record OrphanCleanupItem(
        long Id,
        string ItemKind,
        string State,
        string StorageKey,
        string OriginalStoragePath,
        string? OrphanStoragePath,
        Guid? MediaGuid,
        DateTimeOffset? FinalizedAt,
        DateTimeOffset? ResolvedAt);

    private static RestoreOrphanResponse? ValidateFileRestoreItem(OrphanCleanupItem item)
    {
        if (item.ItemKind != "media_without_metadata")
        {
            return FailureRestore("conflict", "Only media-without-metadata orphan rows can restore files.");
        }

        if (item.State is not ("moved" or "delete_failed"))
        {
            return FailureRestore("conflict", "File restore is only allowed for moved or delete-failed orphan rows.");
        }

        if (item.FinalizedAt is not null || item.ResolvedAt is not null)
        {
            return FailureRestore("conflict", "Finalized or resolved orphan rows cannot be restored.");
        }

        if (string.IsNullOrWhiteSpace(item.OrphanStoragePath))
        {
            return FailureRestore("conflict", "File orphan row does not have an orphan storage path.");
        }

        return null;
    }

    private static RestoreOrphanResponse? ValidateMetadataRestoreItem(OrphanCleanupItem item)
    {
        if (item.ItemKind != "metadata_without_media")
        {
            return FailureRestore("conflict", "Only metadata-without-media orphan rows can restore metadata.");
        }

        if (item.State is not ("detected" or "delete_failed"))
        {
            return FailureRestore("conflict", "Metadata restore is only allowed for detected or delete-failed orphan rows.");
        }

        if (item.FinalizedAt is not null || item.ResolvedAt is not null)
        {
            return FailureRestore("conflict", "Finalized or resolved orphan rows cannot be restored.");
        }

        if (item.MediaGuid is null)
        {
            return FailureRestore("conflict", "Metadata orphan row does not have a media guid.");
        }

        return null;
    }

    private static OrphanCleanupListResponse FailureList(string errorCode, string errorMessage)
        => new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };

    private static RestoreOrphanResponse FailureRestore(string errorCode, string errorMessage)
        => new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };

    private sealed class UnavailableMessageBus : IMessageBus
    {
        public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ISubscription> SubscribeAsync<T>(
            string subject,
            Func<IMessageContext<T>, Task> handler,
            string? queueGroup = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Subscriptions are not available for orphan cleanup tests.");

        public Task<TResponse?> RequestAsync<TRequest, TResponse>(
            string subject,
            TRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Worker message bus is required for orphan file cleanup.");
    }
}

public sealed record OrphanMetadataCleanupResult(
    long RecordedMetadataWithoutMediaCount,
    long RecordedMediaWithoutMetadataCount,
    long ResolvedCount,
    long MovedFileCount,
    long MoveFailedCount,
    long DeletedFileCount,
    long FileDeleteFailedCount,
    long DeletedMediaCount);

public sealed class OrphanCleanupAdminConsumerService(
    IMessageBus messageBus,
    OrphanMetadataCleanupExecutor cleanupExecutor,
    IClock clock,
    ILogger<OrphanCleanupAdminConsumerService> logger) : BackgroundService
{
    private const string QueueGroup = "databridge-orphan-cleanup-admin";
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<OrphanCleanupListRequest>(
            OrphanCleanupSubjects.AdminList,
            HandleListAsync,
            QueueGroup,
            stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<RestoreOrphanRequest>(
            OrphanCleanupSubjects.AdminRestoreFile,
            HandleRestoreFileAsync,
            QueueGroup,
            stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<RestoreOrphanRequest>(
            OrphanCleanupSubjects.AdminRestoreMetadata,
            HandleRestoreMetadataAsync,
            QueueGroup,
            stoppingToken));

        logger.LogInformation("Subscribed to orphan cleanup admin requests.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task HandleListAsync(IMessageContext<OrphanCleanupListRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.ListAsync(context.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing orphan cleanup items.");
            await context.RespondAsync(new OrphanCleanupListResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal orphan cleanup service error."
            });
        }
    }

    private async Task HandleRestoreFileAsync(IMessageContext<RestoreOrphanRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.RestoreFileOrphanAsync(
                context.Message.OrphanId,
                clock.GetCurrentInstant()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed restoring orphan file row {OrphanId}.", context.Message.OrphanId);
            await context.RespondAsync(InternalRestoreError());
        }
    }

    private async Task HandleRestoreMetadataAsync(IMessageContext<RestoreOrphanRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.RestoreMetadataOrphanAsync(
                context.Message.OrphanId,
                clock.GetCurrentInstant()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed restoring orphan metadata row {OrphanId}.", context.Message.OrphanId);
            await context.RespondAsync(InternalRestoreError());
        }
    }

    private static RestoreOrphanResponse InternalRestoreError()
        => new()
        {
            Success = false,
            ErrorCode = "internal_error",
            ErrorMessage = "Internal orphan cleanup service error."
        };
}

/// <summary>
/// Finalizes media whose content files have remained missing long enough for
/// metadata retention to expire. The missing-file clock comes from unresolved
/// filesystem rescan findings; storage deletion is owned by the Worker/storage
/// layer, not this DataBridge database cleanup.
/// </summary>
public sealed class OrphanMetadataCleanupConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    OrphanMetadataCleanupExecutor cleanupExecutor,
    IClock clock,
    ILogger<OrphanMetadataCleanupConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<OrphanMetadataCleanupRequested>(
            Stream,
            ConsumerName.From(BackgroundJobsTopology.OrphanMetadataCleanupConsumer),
            HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<OrphanMetadataCleanupRequested> context)
    {
        var message = context.Message;
        try
        {
            var now = clock.GetCurrentInstant();
            await messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
            {
                Key = message.ScheduleKey,
                AttemptedAt = now
            });

            var result = await cleanupExecutor.CleanupAsync(now, CancellationToken.None);

            logger.LogInformation(
                "Completed orphan cleanup for schedule {ScheduleKey}: recorded {MetadataWithoutMedia} metadata-without-media and {MediaWithoutMetadata} media-without-metadata item(s), resolved {Resolved}, moved {Moved}/{MoveFailed} file orphan(s), deleted {DeletedFiles}/{DeleteFileFailed} file orphan(s), deleted {DeletedMedia} media root row(s).",
                message.ScheduleKey,
                result.RecordedMetadataWithoutMediaCount,
                result.RecordedMediaWithoutMetadataCount,
                result.ResolvedCount,
                result.MovedFileCount,
                result.MoveFailedCount,
                result.DeletedFileCount,
                result.FileDeleteFailedCount,
                result.DeletedMediaCount);

            await messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
            {
                Key = message.ScheduleKey,
                SucceededAt = clock.GetCurrentInstant()
            });

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling orphan metadata cleanup for schedule {ScheduleKey}; nacking", message.ScheduleKey);
            await messageBus.PublishAsync(ScheduleSubjects.MarkFailure, new ScheduleMarkFailureRequestMessage
            {
                Key = message.ScheduleKey,
                FailedAt = clock.GetCurrentInstant()
            });
            await context.NackAsync();
        }
    }
}
