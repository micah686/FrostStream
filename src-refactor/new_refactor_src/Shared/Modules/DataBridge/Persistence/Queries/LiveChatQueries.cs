namespace DataBridge.Persistence.Queries;

internal static class LiveChatQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["LiveChatBackfillConsumerService.LoadCandidatesAsync.1"] = new(
            """
            SELECT DISTINCT ON (v.media_guid)
                v.media_guid, v.storage_key, v.storage_path, v.version_num
            FROM media.media_content_id_versions v
            JOIN metadata.media_metadata mm ON mm.media_guid = v.media_guid
            WHERE mm.was_live = true
              AND (@target_media_guid IS NULL OR v.media_guid = @target_media_guid)
              AND (@force OR NOT EXISTS (
                    SELECT 1 FROM metadata.media_live_chat lc WHERE lc.media_guid = v.media_guid))
            ORDER BY v.media_guid, v.version_num DESC
            """,
            """
            SELECT
                v.media_guid, v.storage_key, v.storage_path, v.version_num
            FROM media_media_content_id_versions v
            JOIN metadata_media_metadata mm ON mm.media_guid = v.media_guid
            WHERE v.version_num = (SELECT max(latest.version_num) FROM media_media_content_id_versions latest WHERE latest.media_guid = v.media_guid) AND mm.was_live = true
              AND (@target_media_guid IS NULL OR v.media_guid = @target_media_guid)
              AND (@force OR NOT EXISTS (
                    SELECT 1 FROM metadata_media_live_chat lc WHERE lc.media_guid = v.media_guid))
            ORDER BY v.media_guid, v.version_num DESC
            """),
        ["LiveChatIngestService.DeleteForMediaAsync.1"] = new(
            """
            DELETE FROM metadata.media_live_chat WHERE media_guid = @media_guid
            """,
            """
            DELETE FROM metadata_media_live_chat WHERE media_guid = @media_guid
            """),
        ["LiveChatIngestService.UpsertMarkerAsync.1"] = new(
            """
            INSERT INTO metadata.media_live_chat
                (media_guid, version_num, message_count, first_offset_ms, last_offset_ms, ingested_at)
            VALUES (@media_guid, @version_num, @message_count, @first_offset_ms, @last_offset_ms, now())
            ON CONFLICT (media_guid) DO UPDATE SET
                version_num = EXCLUDED.version_num,
                message_count = EXCLUDED.message_count,
                first_offset_ms = EXCLUDED.first_offset_ms,
                last_offset_ms = EXCLUDED.last_offset_ms,
                ingested_at = now()
            """,
            """
            INSERT INTO metadata_media_live_chat
                (media_guid, version_num, message_count, first_offset_ms, last_offset_ms, ingested_at)
            VALUES (@media_guid, @version_num, @message_count, @first_offset_ms, @last_offset_ms, (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000))
            ON CONFLICT (media_guid) DO UPDATE SET
                version_num = EXCLUDED.version_num,
                message_count = EXCLUDED.message_count,
                first_offset_ms = EXCLUDED.first_offset_ms,
                last_offset_ms = EXCLUDED.last_offset_ms,
                ingested_at = (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000)
            """),
    };
}
