namespace DataBridge.Persistence.Queries;

internal static class DownloadsQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["DownloadFlowV2Repository.CreateInitialRunAsync.1"] = new(
            """
            INSERT INTO jobs.download_groups
              (group_id, correlation_id, kind, status, source_url, requested_by, storage_key,
               total_jobs, completed_jobs, warning_jobs, failed_jobs, created_at, updated_at)
            VALUES
              ({fs0}, {fs1}, CAST({fs2} AS jobs.download_group_kind),
               CAST({fs3} AS jobs.download_group_status), {fs4}, {fs5},
               {fs6}, 0, 0, 0, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT (correlation_id) DO NOTHING
            """,
            """
            INSERT INTO jobs_download_groups
              (group_id, correlation_id, kind, status, source_url, requested_by, storage_key,
               total_jobs, completed_jobs, warning_jobs, failed_jobs, created_at, updated_at)
            VALUES
              ({fs0}, {fs1}, {fs2},
               {fs3}, {fs4}, {fs5},
               {fs6}, 0, 0, 0, 0, (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000), (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000))
            ON CONFLICT (correlation_id) DO NOTHING
            """),
        ["DownloadFlowV2Repository.OpenProviderCircuitAsync.1"] = new(
            """
            INSERT INTO jobs.download_provider_circuits (provider, is_open, reason, opened_at, cleared_at)
            VALUES ({fs0}, TRUE, {fs1}, CURRENT_TIMESTAMP, NULL)
            ON CONFLICT (provider) DO UPDATE
            SET is_open = TRUE, reason = EXCLUDED.reason, opened_at = CURRENT_TIMESTAMP, cleared_at = NULL
            """,
            """
            INSERT INTO jobs_download_provider_circuits (provider, is_open, reason, opened_at, cleared_at)
            VALUES ({fs0}, TRUE, {fs1}, (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000), NULL)
            ON CONFLICT (provider) DO UPDATE
            SET is_open = TRUE, reason = EXCLUDED.reason, opened_at = (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000), cleared_at = NULL
            """),
        ["DownloadFlowV2Repository.ClearProviderCircuitAsync.1"] = new(
            """
            INSERT INTO jobs.download_provider_circuits
                (provider, is_open, reason, opened_at, cleared_at)
            VALUES ({fs0}, FALSE, NULL, NULL, CURRENT_TIMESTAMP)
            ON CONFLICT (provider) DO UPDATE
            SET is_open = FALSE, cleared_at = CURRENT_TIMESTAMP
            """,
            """
            INSERT INTO jobs_download_provider_circuits
                (provider, is_open, reason, opened_at, cleared_at)
            VALUES ({fs0}, FALSE, NULL, NULL, (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000))
            ON CONFLICT (provider) DO UPDATE
            SET is_open = FALSE, cleared_at = (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000)
            """),
        ["DownloadFlowV2Repository.FindOpenProviderCircuitAsync.1"] = new(
            """
            SELECT provider AS "Value"
            FROM jobs.download_provider_circuits
            WHERE is_open = TRUE
            """,
            """
            SELECT provider AS "Value"
            FROM jobs_download_provider_circuits
            WHERE is_open = TRUE
            """),
        ["DownloadFlowV2Repository.LockJobAsync.1"] = new(
            """
            SELECT * FROM jobs.download_jobs WHERE job_id = {fs0} FOR UPDATE
            """,
            """
            SELECT * FROM jobs_download_jobs WHERE job_id = {fs0}
            """),
        ["UserNotesRepository.ChannelExistsAsync.1"] = new(
            """
            SELECT EXISTS (SELECT 1 FROM metadata.accounts WHERE id = @account_id)
            """,
            """
            SELECT EXISTS (SELECT 1 FROM metadata_accounts WHERE id = @account_id)
            """),
        ["UserPlaylistsRepository.ShiftPositionsUpAsync.1"] = new(
            """
            UPDATE playlists.user_playlist_items
            SET position = position + {fs0}
            WHERE playlist_id = {fs1} AND position >= {fs2}
            """,
            """
            UPDATE playlists_user_playlist_items
            SET position = position + {fs0}
            WHERE playlist_id = {fs1} AND position >= {fs2}
            """),
        ["UserPlaylistsRepository.ShiftPositionsUpAsync.2"] = new(
            """
            UPDATE playlists.user_playlist_items
            SET position = position - {fs0} + 1
            WHERE playlist_id = {fs1} AND position >= {fs2}
            """,
            """
            UPDATE playlists_user_playlist_items
            SET position = position - {fs0} + 1
            WHERE playlist_id = {fs1} AND position >= {fs2}
            """),
        ["UserPlaylistsRepository.ShiftPositionsDownAsync.1"] = new(
            """
            UPDATE playlists.user_playlist_items
            SET position = position + {fs0}
            WHERE playlist_id = {fs1} AND position > {fs2}
            """,
            """
            UPDATE playlists_user_playlist_items
            SET position = position + {fs0}
            WHERE playlist_id = {fs1} AND position > {fs2}
            """),
        ["UserPlaylistsRepository.ShiftPositionsDownAsync.2"] = new(
            """
            UPDATE playlists.user_playlist_items
            SET position = position - {fs0} - 1
            WHERE playlist_id = {fs1} AND position >= {fs2}
            """,
            """
            UPDATE playlists_user_playlist_items
            SET position = position - {fs0} - 1
            WHERE playlist_id = {fs1} AND position >= {fs2}
            """),
        ["DownloadJobsRepository.TryMarkMessageProcessedAsync.1"] = new(
            """
            INSERT INTO jobs.processed_messages (message_id, operation_key, job_id)
            VALUES ({fs0}, {fs1}, {fs2})
            ON CONFLICT (message_id) DO NOTHING
            """,
            """
            INSERT INTO jobs_processed_messages (message_id, operation_key, job_id)
            VALUES ({fs0}, {fs1}, {fs2})
            ON CONFLICT (message_id) DO NOTHING
            """),
        ["DownloadJobsRepository.ReserveVersionAsync.1"] = new(
            """
            INSERT INTO media.media (media_guid) VALUES ({fs0}) ON CONFLICT (media_guid) DO NOTHING
            """,
            """
            INSERT INTO media_media (media_guid) VALUES ({fs0}) ON CONFLICT (media_guid) DO NOTHING
            """),
        ["DownloadJobsRepository.DeleteNewMediaGuidAsync.1"] = new(
            """
            DELETE FROM media.media WHERE media_guid = {fs0}
            """,
            """
            DELETE FROM media_media WHERE media_guid = {fs0}
            """),
    };
}
