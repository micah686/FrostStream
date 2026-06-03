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
