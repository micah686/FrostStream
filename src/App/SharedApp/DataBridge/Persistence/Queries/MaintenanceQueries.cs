namespace DataBridge.Persistence.Queries;

internal static class MaintenanceQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["BackgroundJobConsumerService.HandleDatabaseMaintenanceAsync.1"] = new(
            """
            VACUUM (ANALYZE);
            """,
            """
            VACUUM; PRAGMA optimize;
            """),
        ["BackgroundJobConsumerService.HandleDatabaseMaintenanceReindexAsync.1"] = new(
            """
            REINDEX DATABASE CONCURRENTLY "{fs0}";
            """,
            """
            REINDEX;
            """),
        ["BackgroundJobConsumerService.HandleDatabaseStaleMediaCleanupAsync.1"] = new(
            """
            WITH candidates AS (
                SELECT m.media_guid
                FROM media.media m
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM media.media_content_id_versions civ
                    WHERE civ.media_guid = m.media_guid
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM media.media_source_versions sv
                    JOIN jobs.download_jobs dj ON dj.job_id = sv.latest_job_id
                    WHERE sv.media_guid = m.media_guid
                    AND dj.state::text = ANY(@active_download_job_states)
                )
            ),
            deleted AS (
                DELETE FROM media.media m
                USING candidates c
                WHERE m.media_guid = c.media_guid
                RETURNING m.media_guid
            )
            SELECT count(*)::bigint FROM deleted;
            """,
            """
            WITH candidates AS (
                SELECT m.media_guid
                FROM media_media m
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM media_media_content_id_versions civ
                    WHERE civ.media_guid = m.media_guid
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM media_media_source_versions sv
                    JOIN jobs_download_jobs dj ON dj.job_id = sv.latest_job_id
                    WHERE sv.media_guid = m.media_guid
                    AND dj.state IN (SELECT value FROM json_each(@active_download_job_states))
                )
            )
            DELETE FROM media_media WHERE media_guid IN (SELECT media_guid FROM candidates) ; SELECT changes();
            """),
    };
}
