namespace DataBridge.Persistence.Queries;

internal static class MediaFilesQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["MediaDeleteExecutor.MediaExistsAsync.1"] = new(
            """
            SELECT EXISTS (SELECT 1 FROM media.media WHERE media_guid = @id);
            """,
            """
            SELECT EXISTS (SELECT 1 FROM media_media WHERE media_guid = @id);
            """),
        ["MediaDeleteExecutor.HasActiveDownloadJobAsync.1"] = new(
            """
            SELECT EXISTS (
                SELECT 1
                FROM media.media_source_versions sv
                JOIN jobs.download_jobs dj ON dj.job_id = sv.latest_job_id
                WHERE sv.media_guid = @id
                  AND dj.state::text = ANY(@active_download_job_states)
            );
            """,
            """
            SELECT EXISTS (
                SELECT 1
                FROM media_media_source_versions sv
                JOIN jobs_download_jobs dj ON dj.job_id = sv.latest_job_id
                WHERE sv.media_guid = @id
                  AND dj.state IN (SELECT value FROM json_each(@active_download_job_states))
            );
            """),
        ["MediaDeleteExecutor.CountContentVersionsAsync.1"] = new(
            """
            SELECT
                count(*) FILTER (WHERE storage_key = @key)::int,
                count(*)::int
            FROM media.media_content_id_versions
            WHERE media_guid = @id;
            """,
            """
            SELECT
                count(*) FILTER (WHERE storage_key = @key),
                count(*)
            FROM media_media_content_id_versions
            WHERE media_guid = @id;
            """),
        ["MediaDeleteExecutor.LoadAllMediaFilesAsync.1"] = new(
            """
            SELECT storage_key, storage_path
            FROM media.media_content_id_versions
            WHERE media_guid = @id
            UNION
            SELECT storage_key, thumbnail_storage_path
            FROM metadata.media_metadata
            WHERE media_guid = @id
              AND storage_key IS NOT NULL
              AND thumbnail_storage_path IS NOT NULL
            UNION
            SELECT storage_key, storage_path
            FROM metadata.media_captions
            WHERE media_guid = @id
              AND storage_key IS NOT NULL;
            """,
            """
            SELECT storage_key, storage_path
            FROM media_media_content_id_versions
            WHERE media_guid = @id
            UNION
            SELECT storage_key, thumbnail_storage_path
            FROM metadata_media_metadata
            WHERE media_guid = @id
              AND storage_key IS NOT NULL
              AND thumbnail_storage_path IS NOT NULL
            UNION
            SELECT storage_key, storage_path
            FROM metadata_media_captions
            WHERE media_guid = @id
              AND storage_key IS NOT NULL;
            """),
        ["MediaDeleteExecutor.LoadMediaFilesForKeyAsync.1"] = new(
            """
            SELECT storage_path
            FROM media.media_content_id_versions
            WHERE media_guid = @id AND storage_key = @key
            UNION
            SELECT thumbnail_storage_path
            FROM metadata.media_metadata
            WHERE media_guid = @id AND storage_key = @key AND thumbnail_storage_path IS NOT NULL
            UNION
            SELECT storage_path
            FROM metadata.media_captions
            WHERE media_guid = @id AND storage_key = @key;
            """,
            """
            SELECT storage_path
            FROM media_media_content_id_versions
            WHERE media_guid = @id AND storage_key = @key
            UNION
            SELECT thumbnail_storage_path
            FROM metadata_media_metadata
            WHERE media_guid = @id AND storage_key = @key AND thumbnail_storage_path IS NOT NULL
            UNION
            SELECT storage_path
            FROM metadata_media_captions
            WHERE media_guid = @id AND storage_key = @key;
            """),
        ["MediaDeleteExecutor.DeleteMediaRowAsync.1"] = new(
            """
            DELETE FROM auth.access_policy_media WHERE media_guid = @id;
            DELETE FROM media.media WHERE media_guid = @id;
            """,
            """
            DELETE FROM auth_access_policy_media WHERE media_guid = @id;
            DELETE FROM media_media WHERE media_guid = @id;
            """),
        ["MediaDeleteExecutor.DeleteStorageKeyRowsAsync.1"] = new(
            """
            DELETE FROM media.media_content_id_versions
            WHERE media_guid = @id AND storage_key = @key;

            DELETE FROM metadata.media_captions
            WHERE media_guid = @id AND storage_key = @key;

            UPDATE metadata.media_metadata
            SET storage_key = NULL, thumbnail_storage_path = NULL
            WHERE media_guid = @id AND storage_key = @key;
            """,
            """
            DELETE FROM media_media_content_id_versions
            WHERE media_guid = @id AND storage_key = @key;

            DELETE FROM metadata_media_captions
            WHERE media_guid = @id AND storage_key = @key;

            UPDATE metadata_media_metadata
            SET storage_key = NULL, thumbnail_storage_path = NULL
            WHERE media_guid = @id AND storage_key = @key;
            """),
        ["MediaDeleteExecutor.DeleteLiveChatAsync.1"] = new(
            """
            DELETE FROM metadata.media_live_chat WHERE media_guid = @id;
            """,
            """
            DELETE FROM metadata_media_live_chat WHERE media_guid = @id;
            """),
        ["MediaThumbnailReadService.ResolveAsync.1"] = new(
            """
            SELECT
                media_guid,
                storage_key,
                thumbnail_storage_path
            FROM metadata.media_metadata
            WHERE media_guid = @media_guid
              AND storage_key IS NOT NULL
              AND thumbnail_storage_path IS NOT NULL
            """,
            """
            SELECT
                media_guid,
                storage_key,
                thumbnail_storage_path
            FROM metadata_media_metadata
            WHERE media_guid = @media_guid
              AND storage_key IS NOT NULL
              AND thumbnail_storage_path IS NOT NULL
            """),
        ["MediaThumbnailGenerationService.ListMissingAsync.1"] = new(
            """
            SELECT DISTINCT ON (mm.media_guid)
                mm.media_guid,
                content.storage_key,
                content.storage_path
            FROM metadata.media_metadata mm
            JOIN media.media_content_id_versions content ON content.media_guid = mm.media_guid
            WHERE mm.account_id = @account_id
              AND NULLIF(BTRIM(mm.thumbnail_storage_path), '') IS NULL
              AND (@after_media_guid IS NULL OR mm.media_guid > @after_media_guid)
            ORDER BY mm.media_guid, content.version_num DESC
            LIMIT @limit
            """,
            """
            SELECT
                mm.media_guid,
                content.storage_key,
                content.storage_path
            FROM metadata_media_metadata mm
            JOIN media_media_content_id_versions content ON content.media_guid = mm.media_guid
            WHERE content.version_num = (SELECT max(latest.version_num) FROM media_media_content_id_versions latest WHERE latest.media_guid = content.media_guid) AND mm.account_id = @account_id
              AND NULLIF(trim(mm.thumbnail_storage_path), '') IS NULL
              AND (@after_media_guid IS NULL OR mm.media_guid > @after_media_guid)
            ORDER BY mm.media_guid, content.version_num DESC
            LIMIT @limit
            """),
        ["MediaThumbnailGenerationService.CompleteAsync.1"] = new(
            """
            UPDATE metadata.media_metadata
            SET thumbnail_storage_path = @storage_path,
                storage_key = @storage_key
            WHERE media_guid = @media_guid
              AND NULLIF(BTRIM(thumbnail_storage_path), '') IS NULL
            """,
            """
            UPDATE metadata_media_metadata
            SET thumbnail_storage_path = @storage_path,
                storage_key = @storage_key
            WHERE media_guid = @media_guid
              AND NULLIF(trim(thumbnail_storage_path), '') IS NULL
            """),
        ["AccountAssetReadService.ResolveAsync.1"] = new(
            """
            SELECT
                id,
                storage_key,
                {fs0} AS asset_storage_path
            FROM metadata.accounts
            WHERE id = @account_id
              AND storage_key IS NOT NULL
              AND {fs1} IS NOT NULL
            """,
            """
            SELECT
                id,
                storage_key,
                {fs0} AS asset_storage_path
            FROM metadata_accounts
            WHERE id = @account_id
              AND storage_key IS NOT NULL
              AND {fs1} IS NOT NULL
            """),
        ["MediaCaptionReadService.ListAsync.1"] = new(
            """
            SELECT media_guid, storage_key, storage_path, two_digit_language_code, caption_type::text AS caption_type, name
            FROM metadata.media_captions
            WHERE media_guid = @media_guid AND storage_key IS NOT NULL
            ORDER BY two_digit_language_code, CASE WHEN caption_type::text = 'subtitles' THEN 0 ELSE 1 END, id
            """,
            """
            SELECT media_guid, storage_key, storage_path, two_digit_language_code, caption_type AS caption_type, name
            FROM metadata_media_captions
            WHERE media_guid = @media_guid AND storage_key IS NOT NULL
            ORDER BY two_digit_language_code, CASE WHEN caption_type = 'subtitles' THEN 0 ELSE 1 END, id
            """),
        ["MediaCaptionReadService.ResolveAsync.1"] = new(
            """
            SELECT
                media_guid,
                storage_key,
                storage_path,
                two_digit_language_code,
                caption_type::text AS caption_type,
                name
            FROM metadata.media_captions
            WHERE media_guid = @media_guid
              AND two_digit_language_code = @language_code
              AND storage_key IS NOT NULL
              AND (@caption_type IS NULL OR caption_type::text = @caption_type)
            ORDER BY CASE WHEN caption_type::text = 'subtitles' THEN 0 ELSE 1 END
            LIMIT 1
            """,
            """
            SELECT
                media_guid,
                storage_key,
                storage_path,
                two_digit_language_code,
                caption_type AS caption_type,
                name
            FROM metadata_media_captions
            WHERE media_guid = @media_guid
              AND two_digit_language_code = @language_code
              AND storage_key IS NOT NULL
              AND (@caption_type IS NULL OR caption_type = @caption_type)
            ORDER BY CASE WHEN caption_type = 'subtitles' THEN 0 ELSE 1 END
            LIMIT 1
            """),
        ["MediaDocumentQuery.GetMediaByGuidAsync.1"] = new(
            """
            WHERE mm.media_guid = @media_guid
            """,
            """
            WHERE mm.media_guid = @media_guid
            """),
        ["MediaDocumentQuery.GetMediaByGuidsAsync.1"] = new(
            """
            WHERE mm.media_guid = ANY(@media_guids)
            ORDER BY mm.id
            """,
            """
            WHERE mm.media_guid IN (SELECT value FROM json_each(@media_guids))
            ORDER BY mm.id
            """),
        ["MediaDocumentQuery.GetCommentsByMediaGuidAsync.1"] = new(
            """
            WHERE mc.media_guid = @media_guid
            ORDER BY mc.comment_timestamp, mc.id
            """,
            """
            WHERE mc.media_guid = @media_guid
            ORDER BY mc.comment_timestamp, mc.id
            """),
        ["MediaDocumentQuery.GetCaptionsByMediaGuidAsync.1"] = new(
            """
            WHERE mc.media_guid = @media_guid
              AND mc.storage_key IS NOT NULL
            ORDER BY mc.two_digit_language_code, mc.caption_type, mc.id
            """,
            """
            WHERE mc.media_guid = @media_guid
              AND mc.storage_key IS NOT NULL
            ORDER BY mc.two_digit_language_code, CASE mc.caption_type WHEN 'subtitles' THEN 0 WHEN 'automatic_captions' THEN 1 END, mc.id
            """),
        ["MediaDocumentQuery.GetMediaBatchAsync.1"] = new(
            """
            WHERE mm.id > @last_id
            ORDER BY mm.id
            LIMIT @page_size
            """,
            """
            WHERE mm.id > @last_id
            ORDER BY mm.id
            LIMIT @page_size
            """),
        ["MediaDocumentQuery.GetCommentBatchAsync.1"] = new(
            """
            WHERE mc.id > @last_id
            ORDER BY mc.id
            LIMIT @page_size
            """,
            """
            WHERE mc.id > @last_id
            ORDER BY mc.id
            LIMIT @page_size
            """),
        ["MediaDocumentQuery.GetCaptionBatchAsync.1"] = new(
            """
            WHERE mc.id > @last_id
              AND mc.storage_key IS NOT NULL
            ORDER BY mc.id
            LIMIT @page_size
            """,
            """
            WHERE mc.id > @last_id
              AND mc.storage_key IS NOT NULL
            ORDER BY mc.id
            LIMIT @page_size
            """),
        ["MediaDocumentQuery.CreateMediaCommand.1"] = new(
            """
            SELECT
                mm.id AS row_id,
                mm.media_guid,
                COALESCE(mm.title, '') AS title,
                mm.description,
                mm.thumbnail_storage_path,
                mm.webpage_url,
                EXTRACT(EPOCH FROM mm.release_date)::bigint AS release_date_unix,
                COALESCE(EXTRACT(EPOCH FROM mm.release_date)::bigint, 0) AS release_date_sort,
                EXTRACT(EPOCH FROM mm.metadata_scrape_date)::bigint AS added_at_sort,
                mm.view_count,
                mm.like_count,
                mm.duration,
                mm.was_live,
                mm.availability::text AS availability,
                mm.age_limit,
                v.video_codec,
                v.video_width,
                v.video_height,
                v.hdr_type,
                au.audio_codec,
                au.audio_channels,
                CASE
                    WHEN v.video_height >= 2160 THEN '2160p'
                    WHEN v.video_height >= 1440 THEN '1440p'
                    WHEN v.video_height >= 1080 THEN '1080p'
                    WHEN v.video_height >= 720 THEN '720p'
                    WHEN v.video_height >= 480 THEN '480p'
                    WHEN v.video_height > 0 THEN 'SD'
                    ELSE NULL
                END AS resolution_label,
                a.id AS account_id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.avatar_storage_path AS account_avatar_storage_path,
                COALESCE(
                    (SELECT json_agg(t.tag_name ORDER BY t.tag_name)
                     FROM metadata.media_tags mt
                     JOIN metadata.tags t ON t.id = mt.tag_id
                     WHERE mt.media_metadata_id = mm.id),
                    '[]'::json)::text AS tags_json,
                COALESCE(
                    (SELECT json_agg(c.category_name ORDER BY c.category_name)
                     FROM metadata.media_categories mc
                     JOIN metadata.categories c ON c.id = mc.category_id
                     WHERE mc.media_metadata_id = mm.id),
                    '[]'::json)::text AS categories_json,
                COALESCE(
                    (SELECT json_agg(g.genre_name ORDER BY g.genre_name)
                     FROM metadata.media_genres mg
                     JOIN metadata.genres g ON g.id = mg.genre_id
                     WHERE mg.media_metadata_id = mm.id),
                    '[]'::json)::text AS genres_json,
                COALESCE(
                    (SELECT json_agg(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata.media_artists ma
                     JOIN metadata.artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id),
                    '[]'::json)::text AS artists_json,
                COALESCE(
                    (SELECT json_agg(DISTINCT c.two_digit_language_code ORDER BY c.two_digit_language_code)
                     FROM metadata.media_captions c
                     WHERE c.media_guid = mm.media_guid),
                    '[]'::json)::text AS caption_languages_json
            FROM metadata.media_metadata mm
            JOIN media.media m ON m.media_guid = mm.media_guid
            JOIN metadata.accounts a ON a.id = mm.account_id
            LEFT JOIN LATERAL (
                SELECT ms.codec_name AS video_codec, vsd.width AS video_width,
                       vsd.height AS video_height, vsd.hdr_type
                FROM metadata.media_base mb
                JOIN metadata.media_streams ms
                    ON ms.media_base_id = mb.id AND ms.stream_type = 'video'
                JOIN metadata.video_stream_details vsd ON vsd.media_stream_id = ms.id
                WHERE mb.media_guid = mm.media_guid
                ORDER BY ms.is_primary DESC, ms.id
                LIMIT 1
            ) v ON true
            LEFT JOIN LATERAL (
                SELECT ms.codec_name AS audio_codec, asd.channels AS audio_channels
                FROM metadata.media_base mb
                JOIN metadata.media_streams ms
                    ON ms.media_base_id = mb.id AND ms.stream_type = 'audio'
                JOIN metadata.audio_stream_details asd ON asd.media_stream_id = ms.id
                WHERE mb.media_guid = mm.media_guid
                ORDER BY ms.is_primary DESC, ms.id
                LIMIT 1
            ) au ON true
            {fs0}
            """,
            """
            SELECT
                mm.id AS row_id,
                mm.media_guid,
                COALESCE(mm.title, '') AS title,
                mm.description,
                mm.thumbnail_storage_path,
                mm.webpage_url,
                fs_epoch_round(mm.release_date) AS release_date_unix,
                COALESCE(fs_epoch_round(mm.release_date), 0) AS release_date_sort,
                fs_epoch_round(mm.metadata_scrape_date) AS added_at_sort,
                mm.view_count,
                mm.like_count,
                mm.duration,
                mm.was_live,
                mm.availability AS availability,
                mm.age_limit,
                v.video_codec,
                v.video_width,
                v.video_height,
                v.hdr_type,
                au.audio_codec,
                au.audio_channels,
                CASE
                    WHEN v.video_height >= 2160 THEN '2160p'
                    WHEN v.video_height >= 1440 THEN '1440p'
                    WHEN v.video_height >= 1080 THEN '1080p'
                    WHEN v.video_height >= 720 THEN '720p'
                    WHEN v.video_height >= 480 THEN '480p'
                    WHEN v.video_height > 0 THEN 'SD'
                    ELSE NULL
                END AS resolution_label,
                a.id AS account_id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.avatar_storage_path AS account_avatar_storage_path,
                COALESCE(
                    (SELECT json_group_array(t.tag_name ORDER BY t.tag_name)
                     FROM metadata_media_tags mt
                     JOIN metadata_tags t ON t.id = mt.tag_id
                     WHERE mt.media_metadata_id = mm.id),
                    '[]') AS tags_json,
                COALESCE(
                    (SELECT json_group_array(c.category_name ORDER BY c.category_name)
                     FROM metadata_media_categories mc
                     JOIN metadata_categories c ON c.id = mc.category_id
                     WHERE mc.media_metadata_id = mm.id),
                    '[]') AS categories_json,
                COALESCE(
                    (SELECT json_group_array(g.genre_name ORDER BY g.genre_name)
                     FROM metadata_media_genres mg
                     JOIN metadata_genres g ON g.id = mg.genre_id
                     WHERE mg.media_metadata_id = mm.id),
                    '[]') AS genres_json,
                COALESCE(
                    (SELECT json_group_array(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata_media_artists ma
                     JOIN metadata_artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id),
                    '[]') AS artists_json,
                COALESCE(
                    (SELECT json_group_array(DISTINCT c.two_digit_language_code ORDER BY c.two_digit_language_code)
                     FROM metadata_media_captions c
                     WHERE c.media_guid = mm.media_guid),
                    '[]') AS caption_languages_json
            FROM metadata_media_metadata mm
            JOIN media_media m ON m.media_guid = mm.media_guid
            JOIN metadata_accounts a ON a.id = mm.account_id
            LEFT JOIN (
                SELECT mb.media_guid, row_number() OVER (PARTITION BY mb.media_guid ORDER BY ms.is_primary DESC, ms.id) AS ordinal, ms.codec_name AS video_codec, vsd.width AS video_width,
                       vsd.height AS video_height, vsd.hdr_type
                FROM metadata_media_base mb
                JOIN metadata_media_streams ms
                    ON ms.media_base_id = mb.id AND ms.stream_type = 'video'
                JOIN metadata_video_stream_details vsd ON vsd.media_stream_id = ms.id
            ) v ON v.media_guid = mm.media_guid AND v.ordinal = 1
            LEFT JOIN (
                SELECT mb.media_guid, row_number() OVER (PARTITION BY mb.media_guid ORDER BY ms.is_primary DESC, ms.id) AS ordinal, ms.codec_name AS audio_codec, asd.channels AS audio_channels
                FROM metadata_media_base mb
                JOIN metadata_media_streams ms
                    ON ms.media_base_id = mb.id AND ms.stream_type = 'audio'
                JOIN metadata_audio_stream_details asd ON asd.media_stream_id = ms.id
            ) au ON au.media_guid = mm.media_guid AND au.ordinal = 1
            {fs0}
            """),
        ["MediaDocumentQuery.CreateCommentsCommand.1"] = new(
            """
            SELECT
                mc.id AS row_id,
                mc.comment_id AS id,
                mc.media_guid,
                COALESCE(mc.parent_comment_id, '') AS parent_comment_id,
                mc.text_comment AS text,
                EXTRACT(EPOCH FROM mc.comment_timestamp)::bigint AS comment_timestamp_unix,
                mc.like_count,
                mc.dislike_count,
                mc.is_favorited,
                mc.is_pinned,
                mc.is_uploader,
                a.id AS account_id,
                a.account_name,
                a.account_handle,
                a.platform,
                a.avatar_storage_path AS account_avatar_storage_path
            FROM metadata.media_comments mc
            JOIN media.media m ON m.media_guid = mc.media_guid
            JOIN metadata.accounts a ON a.id = mc.account_id
            {fs0}
            """,
            """
            SELECT
                mc.id AS row_id,
                mc.comment_id AS id,
                mc.media_guid,
                COALESCE(mc.parent_comment_id, '') AS parent_comment_id,
                mc.text_comment AS text,
                fs_epoch_round(mc.comment_timestamp) AS comment_timestamp_unix,
                mc.like_count,
                mc.dislike_count,
                mc.is_favorited,
                mc.is_pinned,
                mc.is_uploader,
                a.id AS account_id,
                a.account_name,
                a.account_handle,
                a.platform,
                a.avatar_storage_path AS account_avatar_storage_path
            FROM metadata_media_comments mc
            JOIN media_media m ON m.media_guid = mc.media_guid
            JOIN metadata_accounts a ON a.id = mc.account_id
            {fs0}
            """),
        ["MediaDocumentQuery.CreateCaptionsCommand.1"] = new(
            """
            SELECT
                mc.id AS row_id,
                mc.media_guid::text || ':' || mc.two_digit_language_code || ':' || mc.caption_type::text AS id,
                mc.media_guid,
                mc.two_digit_language_code AS language_code,
                mc.caption_type::text AS caption_type,
                mc.name,
                mc.storage_path,
                mc.storage_key
            FROM metadata.media_captions mc
            JOIN media.media m ON m.media_guid = mc.media_guid
            {fs0}
            """,
            """
            SELECT
                mc.id AS row_id,
                fs_guid_text(mc.media_guid) || ':' || mc.two_digit_language_code || ':' || mc.caption_type AS id,
                mc.media_guid,
                mc.two_digit_language_code AS language_code,
                mc.caption_type AS caption_type,
                mc.name,
                mc.storage_path,
                mc.storage_key
            FROM metadata_media_captions mc
            JOIN media_media m ON m.media_guid = mc.media_guid
            {fs0}
            """),
    };
}
