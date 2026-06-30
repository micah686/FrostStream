using System.Text.Json;
using Conduit.NATS;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Reconciles Worker-uploaded filesystem listings against the database inventory.
/// The Worker owns storage access; this service owns the database and computes findings
/// in Postgres to avoid shipping large inventories over NATS.
/// </summary>
public sealed class FilesystemRescanConsumerService(
    IMessageBus messageBus,
    Func<string, IObjectStore> objectStoreFactory,
    NpgsqlDataSource dataSource,
    IClock clock,
    ILogger<FilesystemRescanConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-filesystem-rescan";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<FilesystemRescanStorageKeysRequest>(
            messageBus,
            FilesystemRescanSubjects.StorageKeys,
            HandleStorageKeysAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<FilesystemRescanReconcileRequest>(
            messageBus,
            FilesystemRescanSubjects.Reconcile,
            HandleReconcileAsync,
            QueueGroup,
            stoppingToken);

        logger.LogInformation("Subscribed to filesystem rescan storage-key/reconcile subjects.");
    }

    private async Task HandleStorageKeysAsync(IMessageContext<FilesystemRescanStorageKeysRequest> context)
    {
        try
        {
            var storageKeys = await LoadStorageKeysAsync(CancellationToken.None);
            await context.RespondAsync(new FilesystemRescanStorageKeysResponse
            {
                Success = true,
                StorageKeys = storageKeys
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed loading filesystem rescan storage keys.");
            await context.RespondAsync(new FilesystemRescanStorageKeysResponse
            {
                Success = false,
                ErrorMessage = "Failed to load filesystem rescan storage keys."
            });
        }
    }

    private async Task<IReadOnlyList<string>> LoadStorageKeysAsync(CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await using var command = dataSource.CreateCommand("""
            SELECT DISTINCT storage_key
            FROM media.media_content_id_versions
            ORDER BY storage_key;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private async Task HandleReconcileAsync(IMessageContext<FilesystemRescanReconcileRequest> context)
    {
        var message = context.Message;
        var tempFile = Path.Combine(
            Path.GetTempPath(),
            $"froststream-fs-rescan-{Guid.NewGuid():N}.ndjson");

        try
        {
            var objectStore = objectStoreFactory(message.ObjectBucket);
            await using (var file = File.Create(tempFile))
            {
                await objectStore.GetAsync(message.ObjectKey, file, CancellationToken.None);
            }

            var result = await ReconcileAsync(message.StorageKey, tempFile, CancellationToken.None);
            await objectStore.DeleteAsync(message.ObjectKey, CancellationToken.None);

            logger.LogInformation(
                "Filesystem rescan reconciled schedule {ScheduleKey} storage key {StorageKey}: {Missing} missing, {Unexpected} unexpected.",
                message.ScheduleKey,
                message.StorageKey,
                result.MissingCount,
                result.UnexpectedCount);

            await context.RespondAsync(new FilesystemRescanReconcileResponse
            {
                Success = true,
                MissingCount = result.MissingCount,
                UnexpectedCount = result.UnexpectedCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed filesystem rescan reconcile for storage key {StorageKey} object {ObjectBucket}/{ObjectKey}.",
                message.StorageKey,
                message.ObjectBucket,
                message.ObjectKey);

            await context.RespondAsync(new FilesystemRescanReconcileResponse
            {
                Success = false,
                ErrorMessage = "Failed to reconcile filesystem rescan listing."
            });
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed deleting filesystem rescan temp file {TempFile}.", tempFile);
            }
        }
    }

    private async Task<(int MissingCount, int UnexpectedCount)> ReconcileAsync(
        string storageKey,
        string listingFile,
        CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(
                         "CREATE TEMP TABLE tmp_fs_listing(path text NOT NULL) ON COMMIT DROP;",
                         conn,
                         tx))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await CopyListingAsync(conn, listingFile, cancellationToken);
        var counts = await ApplyFindingsAsync(conn, tx, storageKey, cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return counts;
    }

    private static async Task CopyListingAsync(
        NpgsqlConnection conn,
        string listingFile,
        CancellationToken cancellationToken)
    {
        await using var importer = await conn.BeginBinaryImportAsync(
            "COPY tmp_fs_listing (path) FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        await using var stream = File.OpenRead(listingFile);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var path = JsonSerializer.Deserialize<string>(line);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(path, NpgsqlDbType.Text, cancellationToken);
        }

        await importer.CompleteAsync(cancellationToken);
    }

    private async Task<(int MissingCount, int UnexpectedCount)> ApplyFindingsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string storageKey,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        await using var command = new NpgsqlCommand("""
            WITH actual AS (
                SELECT DISTINCT path
                FROM tmp_fs_listing
            ),
            expected_content AS (
                SELECT
                    storage_path,
                    fs_normalize_path(storage_path) AS normalized_path,
                    media_guid
                FROM media.media_content_id_versions
                WHERE storage_key = @storage_key
            ),
            sidecars AS (
                -- Sidecars are scoped to the backend they live on (@storage_key). Without this,
                -- a sidecar recorded for backend B would be treated as expected when scanning
                -- backend A, masking genuinely unexpected files.
                SELECT fs_normalize_path(path) AS normalized_path
                FROM (
                    SELECT thumbnail_storage_path AS path FROM metadata.media_metadata
                        WHERE thumbnail_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT storage_path FROM metadata.media_captions
                        WHERE storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT avatar_storage_path FROM metadata.accounts
                        WHERE avatar_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT banner_storage_path FROM metadata.accounts
                        WHERE banner_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT info_json_storage_path FROM downloads.download_jobs
                        WHERE info_json_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT meta_storage_path FROM downloads.download_jobs
                        WHERE meta_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT meta_storage_path FROM imports.local_import_items
                        WHERE meta_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT info_json_storage_path FROM imports.local_import_items
                        WHERE info_json_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT thumbnail_storage_path FROM imports.local_import_items
                        WHERE thumbnail_storage_path IS NOT NULL AND storage_key = @storage_key
                    UNION
                    SELECT caption_path->>'storagePath' AS path
                    FROM imports.local_import_items item
                    CROSS JOIN LATERAL jsonb_array_elements(item.caption_storage_paths) AS caption_path
                    WHERE item.caption_storage_paths IS NOT NULL AND item.storage_key = @storage_key
                ) source
            ),
            expected_all AS (
                SELECT normalized_path FROM expected_content
                UNION
                SELECT normalized_path FROM sidecars
            ),
            missing_rows AS (
                SELECT
                    storage_path,
                    'MissingFile'::text AS finding_type,
                    media_guid
                FROM expected_content expected
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM actual
                    WHERE actual.path = expected.normalized_path
                )
            ),
            unexpected_rows AS (
                SELECT
                    actual.path AS storage_path,
                    'UnexpectedFile'::text AS finding_type,
                    NULL::uuid AS media_guid
                FROM actual
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM expected_all expected
                    WHERE expected.normalized_path = actual.path
                )
                AND lower(actual.path) NOT LIKE 'orphaned/%'
            ),
            combined_findings AS (
                SELECT storage_path, finding_type, media_guid FROM missing_rows
                UNION ALL
                SELECT storage_path, finding_type, media_guid FROM unexpected_rows
            ),
            new_findings AS (
                SELECT DISTINCT ON (storage_path, finding_type)
                    storage_path,
                    finding_type,
                    media_guid
                FROM combined_findings
                ORDER BY storage_path, finding_type
            ),
            upserted AS (
                INSERT INTO maintenance.filesystem_rescan_findings
                    (storage_key, storage_path, finding_type, media_guid, detected_at, last_seen_at, resolved_at)
                SELECT
                    @storage_key,
                    storage_path,
                    finding_type,
                    media_guid,
                    @now,
                    @now,
                    NULL
                FROM new_findings
                ON CONFLICT (storage_key, storage_path, finding_type)
                DO UPDATE SET
                    media_guid = EXCLUDED.media_guid,
                    last_seen_at = EXCLUDED.last_seen_at,
                    resolved_at = NULL
                RETURNING 1
            ),
            resolved AS (
                UPDATE maintenance.filesystem_rescan_findings existing
                SET resolved_at = @now
                WHERE existing.storage_key = @storage_key
                  AND existing.resolved_at IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM new_findings latest
                      WHERE latest.storage_path = existing.storage_path
                        AND latest.finding_type = existing.finding_type
                  )
                RETURNING 1
            )
            SELECT
                COUNT(*) FILTER (WHERE finding_type = 'MissingFile')::int AS missing_count,
                COUNT(*) FILTER (WHERE finding_type = 'UnexpectedFile')::int AS unexpected_count
            FROM new_findings;
            """, conn, tx);

        command.Parameters.Add("@storage_key", NpgsqlDbType.Text).Value = storageKey;
        command.Parameters.Add("@now", NpgsqlDbType.TimestampTz).Value = now;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
