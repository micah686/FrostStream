namespace DataBridge.Persistence.Queries;

internal static class RenditionsQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["RenditionQueueRepository.QueryAsync.1"] = new(
            """
            WITH combined AS (
                SELECT
                    'Stream'::text AS kind,
                    sr.rendition_id,
                    sr.media_guid,
                    sr.source_version_num,
                    sr.status::text AS status,
                    sr.storage_key,
                    sr.storage_path,
                    sr.size_bytes,
                    sr.duration_seconds,
                    sr.error_message,
                    EXTRACT(EPOCH FROM sr.created_at)::bigint AS created_at,
                    EXTRACT(EPOCH FROM sr.updated_at)::bigint AS updated_at
                FROM media.stream_renditions sr
                UNION ALL
                SELECT
                    'Audio'::text AS kind,
                    ar.rendition_id,
                    ar.media_guid,
                    ar.source_version_num,
                    ar.status::text AS status,
                    ar.storage_key,
                    ar.storage_path,
                    ar.size_bytes,
                    ar.duration_seconds,
                    ar.error_message,
                    EXTRACT(EPOCH FROM ar.created_at)::bigint AS created_at,
                    EXTRACT(EPOCH FROM ar.updated_at)::bigint AS updated_at
                FROM media.audio_renditions ar
            )
            SELECT
                c.kind,
                c.rendition_id,
                c.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled') AS title,
                c.source_version_num,
                c.status,
                c.storage_key,
                c.storage_path,
                c.size_bytes,
                c.duration_seconds,
                c.error_message,
                c.created_at,
                c.updated_at,
                COUNT(*) OVER() AS total_count
            FROM combined c
            LEFT JOIN metadata.media_metadata mm ON mm.media_guid = c.media_guid
            WHERE (@kind::text IS NULL OR c.kind = @kind::text)
              AND (@status::text IS NULL OR c.status = @status::text)
              AND (@storage_key::text IS NULL OR c.storage_key = @storage_key::text)
              AND (@query::text IS NULL
                   OR mm.title ILIKE '%' || @query::text || '%'
                   OR c.media_guid::text ILIKE '%' || @query::text || '%')
            ORDER BY c.created_at DESC, c.rendition_id DESC
            LIMIT @limit OFFSET @offset
            """,
            """
            WITH combined AS (
                SELECT
                    'Stream' AS kind,
                    sr.rendition_id,
                    sr.media_guid,
                    sr.source_version_num,
                    sr.status AS status,
                    sr.storage_key,
                    sr.storage_path,
                    sr.size_bytes,
                    sr.duration_seconds,
                    sr.error_message,
                    fs_epoch_round(sr.created_at) AS created_at,
                    fs_epoch_round(sr.updated_at) AS updated_at
                FROM media_stream_renditions sr
                UNION ALL
                SELECT
                    'Audio' AS kind,
                    ar.rendition_id,
                    ar.media_guid,
                    ar.source_version_num,
                    ar.status AS status,
                    ar.storage_key,
                    ar.storage_path,
                    ar.size_bytes,
                    ar.duration_seconds,
                    ar.error_message,
                    fs_epoch_round(ar.created_at) AS created_at,
                    fs_epoch_round(ar.updated_at) AS updated_at
                FROM media_audio_renditions ar
            )
            SELECT
                c.kind,
                c.rendition_id,
                c.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled') AS title,
                c.source_version_num,
                c.status,
                c.storage_key,
                c.storage_path,
                c.size_bytes,
                c.duration_seconds,
                c.error_message,
                c.created_at,
                c.updated_at,
                COUNT(*) OVER() AS total_count
            FROM combined c
            LEFT JOIN metadata_media_metadata mm ON mm.media_guid = c.media_guid
            WHERE (@kind IS NULL OR c.kind = @kind)
              AND (@status IS NULL OR c.status = @status)
              AND (@storage_key IS NULL OR c.storage_key = @storage_key)
              AND (@query IS NULL
                   OR fs_ilike(mm.title, '%' || @query || '%', '\')
                   OR fs_ilike(fs_guid_text(c.media_guid), '%' || @query || '%', '\'))
            ORDER BY c.created_at DESC, c.rendition_id DESC
            LIMIT @limit OFFSET @offset
            """),
        ["MediaEncodingStatusRepository.ListChannelAsync.1"] = new(
            """
            SELECT COUNT(*) FILTER (WHERE COALESCE(s.is_encoded, false))
            FROM metadata.media_metadata mm
            JOIN LATERAL (
                SELECT storage_key
                FROM media.media_content_id_versions
                WHERE media_guid = mm.media_guid
                ORDER BY version_num DESC
                LIMIT 1
            ) source ON true
            LEFT JOIN media.audio_encoding_status s ON s.media_guid = mm.media_guid
            WHERE mm.account_id = @account_id
              AND (@storage_key::text IS NULL OR source.storage_key = @storage_key::text)
            """,
            """
            SELECT COUNT(*) FILTER (WHERE COALESCE(s.is_encoded, false))
            FROM metadata_media_metadata mm
            JOIN media_media_content_id_versions source ON source.id = (SELECT latest.id FROM media_media_content_id_versions latest WHERE latest.media_guid = mm.media_guid ORDER BY latest.version_num DESC LIMIT 1)
            LEFT JOIN media_audio_encoding_status s ON s.media_guid = mm.media_guid
            WHERE mm.account_id = @account_id
              AND (@storage_key IS NULL OR source.storage_key = @storage_key)
            """),
        ["MediaEncodingStatusRepository.ListChannelAsync.2"] = new(
            """
            SELECT
                mm.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled'),
                COALESCE(s.is_encoded, false),
                s.storage_key,
                s.storage_path,
                EXTRACT(EPOCH FROM s.encoded_at)::bigint,
                COUNT(*) OVER() AS total_count
            FROM metadata.media_metadata mm
            JOIN LATERAL (
                SELECT storage_key
                FROM media.media_content_id_versions
                WHERE media_guid = mm.media_guid
                ORDER BY version_num DESC
                LIMIT 1
            ) source ON true
            LEFT JOIN media.audio_encoding_status s ON s.media_guid = mm.media_guid
            WHERE mm.account_id = @account_id
              AND (@storage_key::text IS NULL OR source.storage_key = @storage_key::text)
              AND (@is_encoded::boolean IS NULL OR COALESCE(s.is_encoded, false) = @is_encoded::boolean)
            ORDER BY mm.media_guid
            LIMIT @limit OFFSET @offset
            """,
            """
            SELECT
                mm.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled'),
                COALESCE(s.is_encoded, false),
                s.storage_key,
                s.storage_path,
                fs_epoch_round(s.encoded_at),
                COUNT(*) OVER() AS total_count
            FROM metadata_media_metadata mm
            JOIN media_media_content_id_versions source ON source.id = (SELECT latest.id FROM media_media_content_id_versions latest WHERE latest.media_guid = mm.media_guid ORDER BY latest.version_num DESC LIMIT 1)
            LEFT JOIN media_audio_encoding_status s ON s.media_guid = mm.media_guid
            WHERE mm.account_id = @account_id
              AND (@storage_key IS NULL OR source.storage_key = @storage_key)
              AND (@is_encoded IS NULL OR COALESCE(s.is_encoded, false) = @is_encoded)
            ORDER BY mm.media_guid
            LIMIT @limit OFFSET @offset
            """),
        ["MediaEncodingStatusRepository.MediaBelongsToAccountAsync.1"] = new(
            """
            SELECT 1 FROM metadata.media_metadata WHERE media_guid = @media_guid AND account_id = @account_id
            """,
            """
            SELECT 1 FROM metadata_media_metadata WHERE media_guid = @media_guid AND account_id = @account_id
            """),
        ["MediaEncodingStatusRepository.ReadAccountIdForMediaAsync.1"] = new(
            """
            SELECT account_id FROM metadata.media_metadata WHERE media_guid = @media_guid
            """,
            """
            SELECT account_id FROM metadata_media_metadata WHERE media_guid = @media_guid
            """),
        ["AudioRenditionRepository.ReadAccountIdForMediaAsync.1"] = new(
            """
            SELECT account_id FROM metadata.media_metadata WHERE media_guid = @media_guid
            """,
            """
            SELECT account_id FROM metadata_media_metadata WHERE media_guid = @media_guid
            """),
        ["AudioRenditionRepository.ReadChannelAccountAsync.1"] = new(
            """
            SELECT id, account_name, account_description, avatar_storage_path
            FROM metadata.accounts
            WHERE id = @account_id
            """,
            """
            SELECT id, account_name, account_description, avatar_storage_path
            FROM metadata_accounts
            WHERE id = @account_id
            """),
        ["AudioRenditionRepository.ReadChannelSourcesAsync.1"] = new(
            """
            SELECT
                mm.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled'),
                mm.description,
                EXTRACT(EPOCH FROM COALESCE(mm.release_date, media_root.created_at))::bigint,
                CASE WHEN mm.duration IS NULL THEN NULL ELSE ROUND(mm.duration)::integer END,
                source.version_num,
                source.storage_key
            FROM metadata.media_metadata mm
            JOIN media.media media_root ON media_root.media_guid = mm.media_guid
            JOIN LATERAL (
                SELECT version_num, storage_key
                FROM media.media_content_id_versions
                WHERE media_guid = mm.media_guid
                ORDER BY version_num DESC
                LIMIT 1
            ) source ON true
            WHERE mm.account_id = @account_id
            ORDER BY COALESCE(mm.release_date, media_root.created_at) DESC, mm.media_guid
            """,
            """
            SELECT
                mm.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled'),
                mm.description,
                fs_epoch_round(COALESCE(mm.release_date, media_root.created_at)),
                CASE WHEN mm.duration IS NULL THEN NULL ELSE ROUND(mm.duration) END,
                source.version_num,
                source.storage_key
            FROM metadata_media_metadata mm
            JOIN media_media media_root ON media_root.media_guid = mm.media_guid
            JOIN media_media_content_id_versions source ON source.id = (SELECT latest.id FROM media_media_content_id_versions latest WHERE latest.media_guid = mm.media_guid ORDER BY latest.version_num DESC LIMIT 1)
            WHERE mm.account_id = @account_id
            ORDER BY COALESCE(mm.release_date, media_root.created_at) DESC, mm.media_guid
            """),
    };
}
