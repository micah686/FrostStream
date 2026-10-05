namespace DataBridge.Persistence.Queries;

internal static class MetadataReadQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["MetadataReadService.GetDetailAsync.1"] = new(
            """
            SELECT
                mm.media_guid,
                COALESCE(mm.title, '') AS title,
                mm.description,
                mm.thumbnail_storage_path,
                mm.duration,
                mm.release_date,
                mm.view_count,
                mm.like_count,
                mm.dislike_count,
                mm.average_rating,
                mm.comment_count,
                mm.age_limit,
                mm.was_live,
                mm.availability::text AS availability,
                mm.location,
                mm.webpage_url,
                mm.external_media_id,
                mm.metadata_scrape_date,
                a.id AS account_id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_creation_date,
                a.account_follower_count,
                a.is_verified,
                a.account_description,
                a.avatar_storage_path,
                a.banner_storage_path,
                (SELECT COUNT(*) FROM metadata.media_metadata amm WHERE amm.account_id = a.id) AS account_media_count,
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
                    (SELECT json_agg(cm.cast_name ORDER BY cm.cast_name)
                     FROM metadata.media_cast mc
                     JOIN metadata.cast_members cm ON cm.id = mc.cast_member_id
                     WHERE mc.media_metadata_id = mm.id),
                    '[]'::json)::text AS cast_json,
                COALESCE(
                    (SELECT json_agg(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata.media_artists ma
                     JOIN metadata.artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id AND ma.is_album_artist = false),
                    '[]'::json)::text AS artists_json,
                COALESCE(
                    (SELECT json_agg(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata.media_artists ma
                     JOIN metadata.artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id AND ma.is_album_artist = true),
                    '[]'::json)::text AS album_artists_json,
                COALESCE(
                    (SELECT json_agg(row_to_json(caption_rows))
                     FROM (
                         SELECT DISTINCT
                             c.two_digit_language_code AS "languageCode",
                             c.caption_type::text AS "captionType",
                             c.name AS "name"
                         FROM metadata.media_captions c
                         WHERE c.media_guid = mm.media_guid
                         ORDER BY c.two_digit_language_code, c.caption_type::text
                     ) caption_rows),
                    '[]'::json)::text AS caption_languages_json,
                EXISTS (SELECT 1 FROM metadata.media_live_chat lc
                        WHERE lc.media_guid = mm.media_guid) AS has_live_chat,
                s.series_name,
                s.season_count,
                s.season_number,
                s.season_name,
                s.episode_number,
                s.episode_name,
                m.album_title,
                m.album_type,
                m.disc_number,
                m.release_year,
                m.track_title,
                m.track_number,
                m.composer
            FROM metadata.media_metadata mm
            JOIN metadata.accounts a ON a.id = mm.account_id
            LEFT JOIN metadata.series_metadata s ON s.media_guid = mm.media_guid
            LEFT JOIN metadata.music_metadata m ON m.media_guid = mm.media_guid
            WHERE mm.media_guid = @media_guid
            """,
            """
            SELECT
                mm.media_guid,
                COALESCE(mm.title, '') AS title,
                mm.description,
                mm.thumbnail_storage_path,
                mm.duration,
                mm.release_date,
                mm.view_count,
                mm.like_count,
                mm.dislike_count,
                mm.average_rating,
                mm.comment_count,
                mm.age_limit,
                mm.was_live,
                mm.availability AS availability,
                mm.location,
                mm.webpage_url,
                mm.external_media_id,
                mm.metadata_scrape_date,
                a.id AS account_id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_creation_date,
                a.account_follower_count,
                a.is_verified,
                a.account_description,
                a.avatar_storage_path,
                a.banner_storage_path,
                (SELECT COUNT(*) FROM metadata_media_metadata amm WHERE amm.account_id = a.id) AS account_media_count,
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
                    (SELECT json_group_array(cm.cast_name ORDER BY cm.cast_name)
                     FROM metadata_media_cast mc
                     JOIN metadata_cast_members cm ON cm.id = mc.cast_member_id
                     WHERE mc.media_metadata_id = mm.id),
                    '[]') AS cast_json,
                COALESCE(
                    (SELECT json_group_array(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata_media_artists ma
                     JOIN metadata_artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id AND ma.is_album_artist = false),
                    '[]') AS artists_json,
                COALESCE(
                    (SELECT json_group_array(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata_media_artists ma
                     JOIN metadata_artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id AND ma.is_album_artist = true),
                    '[]') AS album_artists_json,
                COALESCE(
                    (SELECT json_group_array(json_object('languageCode', caption_rows.languageCode, 'captionType', caption_rows.captionType, 'name', caption_rows.name))
                     FROM (
                         SELECT DISTINCT
                             c.two_digit_language_code AS "languageCode",
                             c.caption_type AS "captionType",
                             c.name AS "name"
                         FROM metadata_media_captions c
                         WHERE c.media_guid = mm.media_guid
                         ORDER BY c.two_digit_language_code, c.caption_type
                     ) caption_rows),
                    '[]') AS caption_languages_json,
                EXISTS (SELECT 1 FROM metadata_media_live_chat lc
                        WHERE lc.media_guid = mm.media_guid) AS has_live_chat,
                s.series_name,
                s.season_count,
                s.season_number,
                s.season_name,
                s.episode_number,
                s.episode_name,
                m.album_title,
                m.album_type,
                m.disc_number,
                m.release_year,
                m.track_title,
                m.track_number,
                m.composer
            FROM metadata_media_metadata mm
            JOIN metadata_accounts a ON a.id = mm.account_id
            LEFT JOIN metadata_series_metadata s ON s.media_guid = mm.media_guid
            LEFT JOIN metadata_music_metadata m ON m.media_guid = mm.media_guid
            WHERE mm.media_guid = @media_guid
            """),
        ["MetadataReadService.GetRandomMediaGuidAsync.1"] = new(
            """
            SELECT mm.media_guid
            FROM metadata.media_metadata mm
            WHERE @exclude_media_guid IS NULL OR mm.media_guid <> @exclude_media_guid
            ORDER BY random()
            LIMIT 1
            """,
            """
            SELECT mm.media_guid
            FROM metadata_media_metadata mm
            WHERE @exclude_media_guid IS NULL OR mm.media_guid <> @exclude_media_guid
            ORDER BY random()
            LIMIT 1
            """),
        ["MetadataReadService.GetTechnicalAsync.1"] = new(
            """
            SELECT
                mb.media_guid,
                mb.duration_ticks,
                mfd.duration_ticks AS format_duration_ticks,
                mfd.start_time_ticks,
                mfd.format_long_names,
                mfd.stream_count,
                mfd.bit_rate AS format_bit_rate
            FROM metadata.media_base mb
            LEFT JOIN metadata.media_format_details mfd ON mfd.media_base_id = mb.id
            WHERE mb.media_guid = @media_guid
            """,
            """
            SELECT
                mb.media_guid,
                mb.duration_ticks,
                mfd.duration_ticks AS format_duration_ticks,
                mfd.start_time_ticks,
                mfd.format_long_names,
                mfd.stream_count,
                mfd.bit_rate AS format_bit_rate
            FROM metadata_media_base mb
            LEFT JOIN metadata_media_format_details mfd ON mfd.media_base_id = mb.id
            WHERE mb.media_guid = @media_guid
            """),
        ["MetadataReadService.GetTechnicalAsync.2"] = new(
            """
            SELECT
                ms.stream_type,
                ms.is_primary,
                ms.codec_name,
                ms.codec_long_name,
                ms.bit_rate,
                ms.bit_depth,
                ms.duration_ticks,
                ms.language,
                vs.width,
                vs.height,
                vs.avg_frame_rate,
                vs.hdr_type,
                vs.color_space,
                vs.profile AS video_profile,
                ads.channels,
                ads.channel_layout,
                ads.sample_rate_hz,
                ads.profile AS audio_profile
            FROM metadata.media_base mb
            JOIN metadata.media_streams ms ON ms.media_base_id = mb.id
            LEFT JOIN metadata.video_stream_details vs ON vs.media_stream_id = ms.id
            LEFT JOIN metadata.audio_stream_details ads ON ads.media_stream_id = ms.id
            WHERE mb.media_guid = @media_guid
            ORDER BY ms.id
            """,
            """
            SELECT
                ms.stream_type,
                ms.is_primary,
                ms.codec_name,
                ms.codec_long_name,
                ms.bit_rate,
                ms.bit_depth,
                ms.duration_ticks,
                ms.language,
                vs.width,
                vs.height,
                vs.avg_frame_rate,
                vs.hdr_type,
                vs.color_space,
                vs.profile AS video_profile,
                ads.channels,
                ads.channel_layout,
                ads.sample_rate_hz,
                ads.profile AS audio_profile
            FROM metadata_media_base mb
            JOIN metadata_media_streams ms ON ms.media_base_id = mb.id
            LEFT JOIN metadata_video_stream_details vs ON vs.media_stream_id = ms.id
            LEFT JOIN metadata_audio_stream_details ads ON ads.media_stream_id = ms.id
            WHERE mb.media_guid = @media_guid
            ORDER BY ms.id
            """),
        ["MetadataReadService.GetTechnicalAsync.3"] = new(
            """
            SELECT cd.title, cd.start_ticks, cd.end_ticks
            FROM metadata.media_base mb
            JOIN metadata.chapter_data cd ON cd.media_base_id = mb.id
            WHERE mb.media_guid = @media_guid
            ORDER BY cd.start_ticks, cd.id
            """,
            """
            SELECT cd.title, cd.start_ticks, cd.end_ticks
            FROM metadata_media_base mb
            JOIN metadata_chapter_data cd ON cd.media_base_id = mb.id
            WHERE mb.media_guid = @media_guid
            ORDER BY cd.start_ticks, cd.id
            """),
        ["MetadataReadService.ListVersionsAsync.1"] = new(
            """
            SELECT
                media_guid,
                version_num,
                storage_key,
                storage_path,
                content_hash_xxh128,
                ingest_origin::text AS ingest_origin
            FROM media.media_content_id_versions
            WHERE media_guid = @media_guid
            ORDER BY version_num
            """,
            """
            SELECT
                media_guid,
                version_num,
                storage_key,
                storage_path,
                content_hash_xxh128,
                ingest_origin AS ingest_origin
            FROM media_media_content_id_versions
            WHERE media_guid = @media_guid
            ORDER BY version_num
            """),
        ["MetadataReadService.ListAccountsAsync.1"] = new(
            """
            SELECT
                a.id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_follower_count,
                a.is_verified,
                a.avatar_storage_path,
                COUNT(mm.id) AS media_count
            FROM metadata.accounts a
            JOIN metadata.media_metadata mm ON mm.account_id = a.id
            WHERE (@platform IS NULL OR a.platform = @platform)
              AND (
                  @after_handle IS NULL
                  OR (a.account_handle, a.id) > (@after_handle, @after_id)
              )
            GROUP BY a.id
            ORDER BY a.account_handle, a.id
            LIMIT @page_size
            """,
            """
            SELECT
                a.id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_follower_count,
                a.is_verified,
                a.avatar_storage_path,
                COUNT(mm.id) AS media_count
            FROM metadata_accounts a
            JOIN metadata_media_metadata mm ON mm.account_id = a.id
            WHERE (@platform IS NULL OR a.platform = @platform)
              AND (
                  @after_handle IS NULL
                  OR (a.account_handle, a.id) > (@after_handle, @after_id)
              )
            GROUP BY a.id
            ORDER BY a.account_handle, a.id
            LIMIT @page_size
            """),
        ["MetadataReadService.GetAccountAsync.1"] = new(
            """
            SELECT
                a.id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_creation_date,
                a.account_follower_count,
                a.is_verified,
                a.account_description,
                a.avatar_storage_path,
                a.banner_storage_path,
                COUNT(mm.id) AS media_count
            FROM metadata.accounts a
            LEFT JOIN metadata.media_metadata mm ON mm.account_id = a.id
            WHERE a.id = @account_id
            GROUP BY a.id
            """,
            """
            SELECT
                a.id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_creation_date,
                a.account_follower_count,
                a.is_verified,
                a.account_description,
                a.avatar_storage_path,
                a.banner_storage_path,
                COUNT(mm.id) AS media_count
            FROM metadata_accounts a
            LEFT JOIN metadata_media_metadata mm ON mm.account_id = a.id
            WHERE a.id = @account_id
            GROUP BY a.id
            """),
        ["MetadataReadService.ListTaxonomyAsync.1"] = new(
            """
            SELECT COUNT(*)
            FROM {fs0} t
            WHERE (@search IS NULL OR t.{fs1} ILIKE @search || '%')
            """,
            """
            SELECT COUNT(*)
            FROM {fs0} t
            WHERE (@search IS NULL OR fs_ilike(t.{fs1}, @search || '%', '\'))
            """),
        ["MetadataReadService.ListTaxonomyAsync.2"] = new(
            """
            SELECT
                t.{fs0} AS name,
                COUNT(j.media_metadata_id) AS media_count
            FROM {fs1} t
            LEFT JOIN {fs2} j ON j.{fs3} = t.id
            WHERE (@search IS NULL OR t.{fs4} ILIKE @search || '%')
            GROUP BY t.id, t.{fs5}
            ORDER BY t.{fs6}
            LIMIT @page_size OFFSET @page_offset
            """,
            """
            SELECT
                t.{fs0} AS name,
                COUNT(j.media_metadata_id) AS media_count
            FROM {fs1} t
            LEFT JOIN {fs2} j ON j.{fs3} = t.id
            WHERE (@search IS NULL OR fs_ilike(t.{fs4}, @search || '%', '\'))
            GROUP BY t.id, t.{fs5}
            ORDER BY t.{fs6}
            LIMIT @page_size OFFSET @page_offset
            """),
    };
}
