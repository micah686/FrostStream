namespace DataBridge.Persistence.Queries;

internal static class MetadataWriteQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["MetadataRepository.UpsertAccountAssetsAsync.1"] = new(
            """
            INSERT INTO metadata.accounts
                (platform, account_name, account_handle, account_url, is_verified,
                 avatar_storage_path, banner_storage_path, storage_key)
            VALUES
                (@platform, @account_name, @account_handle, @account_url, false,
                 @avatar_path, @banner_path, @storage_key)
            ON CONFLICT (platform, account_handle) DO UPDATE SET
                account_url         = COALESCE(EXCLUDED.account_url, accounts.account_url),
                avatar_storage_path = COALESCE(EXCLUDED.avatar_storage_path, accounts.avatar_storage_path),
                banner_storage_path = COALESCE(EXCLUDED.banner_storage_path, accounts.banner_storage_path),
                storage_key         = COALESCE(EXCLUDED.storage_key, accounts.storage_key)
            RETURNING id
            """,
            """
            INSERT INTO metadata_accounts
                (platform, account_name, account_handle, account_url, is_verified,
                 avatar_storage_path, banner_storage_path, storage_key)
            VALUES
                (@platform, @account_name, @account_handle, @account_url, false,
                 @avatar_path, @banner_path, @storage_key)
            ON CONFLICT (platform, account_handle) DO UPDATE SET
                account_url         = COALESCE(EXCLUDED.account_url, metadata_accounts.account_url),
                avatar_storage_path = COALESCE(EXCLUDED.avatar_storage_path, metadata_accounts.avatar_storage_path),
                banner_storage_path = COALESCE(EXCLUDED.banner_storage_path, metadata_accounts.banner_storage_path),
                storage_key         = COALESCE(EXCLUDED.storage_key, metadata_accounts.storage_key)
            RETURNING id
            """),
        ["MetadataRepository.UpdateAccountAssetsByIdAsync.1"] = new(
            """
            UPDATE metadata.accounts SET
                avatar_storage_path = COALESCE(@avatar_path, avatar_storage_path),
                banner_storage_path = COALESCE(@banner_path, banner_storage_path),
                storage_key         = COALESCE(@storage_key, storage_key)
            WHERE id = @account_id
            """,
            """
            UPDATE metadata_accounts SET
                avatar_storage_path = COALESCE(@avatar_path, avatar_storage_path),
                banner_storage_path = COALESCE(@banner_path, banner_storage_path),
                storage_key         = COALESCE(@storage_key, storage_key)
            WHERE id = @account_id
            """),
        ["MetadataRepository.UpsertAccountAsync.1"] = new(
            """
            UPDATE metadata.accounts SET
                account_name       = COALESCE(NULLIF(@account_name, ''), account_name),
                account_url        = COALESCE(@account_url, account_url),
                account_follower_count = COALESCE(@follower_count, account_follower_count),
                account_description = COALESCE(@description, account_description)
            WHERE id = @account_id
            """,
            """
            UPDATE metadata_accounts SET
                account_name       = COALESCE(NULLIF(@account_name, ''), account_name),
                account_url        = COALESCE(@account_url, account_url),
                account_follower_count = COALESCE(@follower_count, account_follower_count),
                account_description = COALESCE(@description, account_description)
            WHERE id = @account_id
            """),
        ["MetadataRepository.UpsertAccountAsync.2"] = new(
            """
            INSERT INTO metadata.accounts
                (platform, account_name, account_handle, account_url, account_follower_count, is_verified, account_description)
            VALUES
                (@platform, @account_name, @account_handle, @account_url, @follower_count, false, @description)
            ON CONFLICT (platform, account_handle) DO UPDATE SET
                account_name       = COALESCE(NULLIF(EXCLUDED.account_name, ''), metadata.accounts.account_name),
                account_url        = COALESCE(EXCLUDED.account_url, metadata.accounts.account_url),
                account_follower_count = EXCLUDED.account_follower_count,
                account_description = EXCLUDED.account_description
            RETURNING id
            """,
            """
            INSERT INTO metadata_accounts
                (platform, account_name, account_handle, account_url, account_follower_count, is_verified, account_description)
            VALUES
                (@platform, @account_name, @account_handle, @account_url, @follower_count, false, @description)
            ON CONFLICT (platform, account_handle) DO UPDATE SET
                account_name       = COALESCE(NULLIF(EXCLUDED.account_name, ''), metadata_accounts.account_name),
                account_url        = COALESCE(EXCLUDED.account_url, metadata_accounts.account_url),
                account_follower_count = EXCLUDED.account_follower_count,
                account_description = EXCLUDED.account_description
            RETURNING id
            """),
        ["MetadataRepository.FindAccountIdByExternalIdsAsync.1"] = new(
            """
            SELECT account_id
            FROM metadata.account_external_ids
            WHERE platform = @platform
              AND external_id = @external_id
            LIMIT 1
            """,
            """
            SELECT account_id
            FROM metadata_account_external_ids
            WHERE platform = @platform
              AND external_id = @external_id
            LIMIT 1
            """),
        ["MetadataRepository.AddAccountExternalIdsAsync.1"] = new(
            """
            INSERT INTO metadata.account_external_ids
                (account_id, platform, id_kind, external_id)
            VALUES
                (@account_id, @platform, @id_kind, @external_id)
            ON CONFLICT (platform, external_id) DO NOTHING
            """,
            """
            INSERT INTO metadata_account_external_ids
                (account_id, platform, id_kind, external_id)
            VALUES
                (@account_id, @platform, @id_kind, @external_id)
            ON CONFLICT (platform, external_id) DO NOTHING
            """),
        ["MetadataRepository.InsertMediaMetadataAsync.1"] = new(
            """
            DELETE FROM metadata.media_metadata WHERE media_guid = @media_guid
            """,
            """
            DELETE FROM metadata_media_metadata WHERE media_guid = @media_guid
            """),
        ["MetadataRepository.InsertMediaMetadataAsync.2"] = new(
            """
            INSERT INTO metadata.media_metadata
                (media_guid, account_id, external_media_id, metadata_scrape_date,
                 thumbnail_storage_path, storage_key, age_limit, average_rating, like_count, dislike_count,
                 duration, description, release_date, title, was_live, webpage_url,
                 view_count, comment_count, availability, location)
            VALUES
                (@media_guid, @account_id, @external_media_id, @scrape_date,
                 @thumbnail, @storage_key, @age_limit, @avg_rating, @like_count, @dislike_count,
                 @duration, @description, @release_date, @title, @was_live, @webpage_url,
                 @view_count, @comment_count,
                 CAST(NULLIF(@availability, '') AS metadata.availability_enum),
                 @location)
            RETURNING id
            """,
            """
            INSERT INTO metadata_media_metadata
                (media_guid, account_id, external_media_id, metadata_scrape_date,
                 thumbnail_storage_path, storage_key, age_limit, average_rating, like_count, dislike_count,
                 duration, description, release_date, title, was_live, webpage_url,
                 view_count, comment_count, availability, location)
            VALUES
                (@media_guid, @account_id, @external_media_id, @scrape_date,
                 @thumbnail, @storage_key, @age_limit, @avg_rating, @like_count, @dislike_count,
                 @duration, @description, @release_date, @title, @was_live, @webpage_url,
                 @view_count, @comment_count,
                 NULLIF(@availability, ''),
                 @location)
            RETURNING id
            """),
        ["MetadataRepository.InsertTaxonomyAsync.1"] = new(
            """
            INSERT INTO metadata.media_artists (media_metadata_id, artist_id, is_album_artist) VALUES (@mm, @ref, false) ON CONFLICT DO NOTHING
            """,
            """
            INSERT INTO metadata_media_artists (media_metadata_id, artist_id, is_album_artist) VALUES (@mm, @ref, false) ON CONFLICT DO NOTHING
            """),
        ["MetadataRepository.InsertTaxonomyAsync.2"] = new(
            """
            INSERT INTO metadata.media_artists (media_metadata_id, artist_id, is_album_artist) VALUES (@mm, @ref, true) ON CONFLICT DO NOTHING
            """,
            """
            INSERT INTO metadata_media_artists (media_metadata_id, artist_id, is_album_artist) VALUES (@mm, @ref, true) ON CONFLICT DO NOTHING
            """),
        ["MetadataRepository.InsertTaxonomyAsync.3"] = new(
            """
            INSERT INTO metadata.media_genres (media_metadata_id, genre_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """,
            """
            INSERT INTO metadata_media_genres (media_metadata_id, genre_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """),
        ["MetadataRepository.InsertTaxonomyAsync.4"] = new(
            """
            INSERT INTO metadata.media_tags (media_metadata_id, tag_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """,
            """
            INSERT INTO metadata_media_tags (media_metadata_id, tag_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """),
        ["MetadataRepository.InsertTaxonomyAsync.5"] = new(
            """
            INSERT INTO metadata.media_categories (media_metadata_id, category_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """,
            """
            INSERT INTO metadata_media_categories (media_metadata_id, category_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """),
        ["MetadataRepository.InsertTaxonomyAsync.6"] = new(
            """
            INSERT INTO metadata.media_cast (media_metadata_id, cast_member_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """,
            """
            INSERT INTO metadata_media_cast (media_metadata_id, cast_member_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING
            """),
        ["MetadataRepository.UpsertLookupAsync.1"] = new(
            """
            INSERT INTO {fs0} ({fs1}) VALUES (@v) ON CONFLICT ({fs2}) DO UPDATE SET {fs3} = EXCLUDED.{fs4} RETURNING id
            """,
            """
            INSERT INTO {fs0} ({fs1}) VALUES (@v) ON CONFLICT ({fs2}) DO UPDATE SET {fs3} = EXCLUDED.{fs4} RETURNING id
            """),
        ["MetadataRepository.InsertTechnicalAsync.1"] = new(
            """
            DELETE FROM metadata.media_base WHERE media_guid = @media_guid
            """,
            """
            DELETE FROM metadata_media_base WHERE media_guid = @media_guid
            """),
        ["MetadataRepository.InsertTechnicalAsync.2"] = new(
            """
            INSERT INTO metadata.media_base (media_guid, duration_ticks) VALUES (@media_guid, @ticks) RETURNING id
            """,
            """
            INSERT INTO metadata_media_base (media_guid, duration_ticks) VALUES (@media_guid, @ticks) RETURNING id
            """),
        ["MetadataRepository.InsertTechnicalAsync.3"] = new(
            """
            INSERT INTO metadata.media_format_details
                (media_base_id, duration_ticks, start_time_ticks, format_long_names, stream_count, bit_rate)
            VALUES (@base_id, @dur, @start, @names, @streams, @bitrate)
            """,
            """
            INSERT INTO metadata_media_format_details
                (media_base_id, duration_ticks, start_time_ticks, format_long_names, stream_count, bit_rate)
            VALUES (@base_id, @dur, @start, @names, @streams, @bitrate)
            """),
        ["MetadataRepository.InsertTechnicalAsync.4"] = new(
            """
            INSERT INTO metadata.media_streams
                (media_base_id, stream_type, is_primary, codec_name, codec_long_name,
                 bit_rate, bit_depth, start_time_ticks, duration_ticks, language)
            VALUES (@base_id, @type, @primary, @codec, @codec_long,
                    @bitrate, @depth, 0, @dur, @lang)
            RETURNING id
            """,
            """
            INSERT INTO metadata_media_streams
                (media_base_id, stream_type, is_primary, codec_name, codec_long_name,
                 bit_rate, bit_depth, start_time_ticks, duration_ticks, language)
            VALUES (@base_id, @type, @primary, @codec, @codec_long,
                    @bitrate, @depth, 0, @dur, @lang)
            RETURNING id
            """),
        ["MetadataRepository.InsertTechnicalAsync.5"] = new(
            """
            INSERT INTO metadata.video_stream_details
                (media_stream_id, avg_frame_rate, bits_per_raw_sample,
                 display_aspect_ratio_width, display_aspect_ratio_height,
                 profile, width, height, pixel_format, rotation,
                 color_space, color_transfer, color_primaries, hdr_type)
            VALUES (@id, @fps, 0, 0, 0, '', @w, @h, '', 0, '', '', '', @hdr)
            """,
            """
            INSERT INTO metadata_video_stream_details
                (media_stream_id, avg_frame_rate, bits_per_raw_sample,
                 display_aspect_ratio_width, display_aspect_ratio_height,
                 profile, width, height, pixel_format, rotation,
                 color_space, color_transfer, color_primaries, hdr_type)
            VALUES (@id, @fps, 0, 0, 0, '', @w, @h, '', 0, '', '', '', @hdr)
            """),
        ["MetadataRepository.InsertTechnicalAsync.6"] = new(
            """
            INSERT INTO metadata.audio_stream_details
                (media_stream_id, channels, channel_layout, sample_rate_hz, profile)
            VALUES (@id, @channels, '', @rate, '')
            """,
            """
            INSERT INTO metadata_audio_stream_details
                (media_stream_id, channels, channel_layout, sample_rate_hz, profile)
            VALUES (@id, @channels, '', @rate, '')
            """),
        ["MetadataRepository.InsertTechnicalAsync.7"] = new(
            """
            INSERT INTO metadata.chapter_data (media_base_id, title, start_ticks, end_ticks)
            VALUES (@base_id, @title, @start, @end)
            """,
            """
            INSERT INTO metadata_chapter_data (media_base_id, title, start_ticks, end_ticks)
            VALUES (@base_id, @title, @start, @end)
            """),
        ["MetadataRepository.InsertCaptionsAsync.1"] = new(
            """
            DELETE FROM metadata.media_captions WHERE media_guid = @media_guid
            """,
            """
            DELETE FROM metadata_media_captions WHERE media_guid = @media_guid
            """),
        ["MetadataRepository.InsertCaptionsAsync.2"] = new(
            """
            INSERT INTO metadata.media_captions
                (media_guid, storage_path, storage_key, caption_type, two_digit_language_code, name)
            VALUES
                (@media_guid, @path, @storage_key, @type::metadata.subtitle_type_enum, @lang, @name)
            """,
            """
            INSERT INTO metadata_media_captions
                (media_guid, storage_path, storage_key, caption_type, two_digit_language_code, name)
            VALUES
                (@media_guid, @path, @storage_key, @type, @lang, @name)
            """),
        ["MetadataRepository.InsertCommentsAsync.1"] = new(
            """
            DELETE FROM metadata.media_comments WHERE media_guid = @media_guid
            """,
            """
            DELETE FROM metadata_media_comments WHERE media_guid = @media_guid
            """),
        ["MetadataRepository.InsertCommentsAsync.2"] = new(
            """
            INSERT INTO metadata.media_comments
                (media_guid, comment_id, parent_comment_id, text_comment, account_id,
                 comment_timestamp, like_count, dislike_count, is_favorited, is_uploader, is_pinned)
            VALUES
                (@media_guid, @comment_id, @parent_id, @text, @account_id,
                 @timestamp, @like_count, @dislike_count, @is_favorited, @is_uploader, @is_pinned)
            """,
            """
            INSERT INTO metadata_media_comments
                (media_guid, comment_id, parent_comment_id, text_comment, account_id,
                 comment_timestamp, like_count, dislike_count, is_favorited, is_uploader, is_pinned)
            VALUES
                (@media_guid, @comment_id, @parent_id, @text, @account_id,
                 @timestamp, @like_count, @dislike_count, @is_favorited, @is_uploader, @is_pinned)
            """),
        ["MetadataRepository.InsertSeriesAsync.1"] = new(
            """
            DELETE FROM metadata.series_metadata WHERE media_guid = @media_guid
            """,
            """
            DELETE FROM metadata_series_metadata WHERE media_guid = @media_guid
            """),
        ["MetadataRepository.InsertSeriesAsync.2"] = new(
            """
            INSERT INTO metadata.series_metadata
                (media_guid, series_name, season_count, season_number, season_name, episode_number, episode_name)
            VALUES
                (@media_guid, @series_name, @season_count, @season_num, @season_name, @episode_num, @episode_name)
            """,
            """
            INSERT INTO metadata_series_metadata
                (media_guid, series_name, season_count, season_number, season_name, episode_number, episode_name)
            VALUES
                (@media_guid, @series_name, @season_count, @season_num, @season_name, @episode_num, @episode_name)
            """),
        ["MetadataRepository.InsertMusicAsync.1"] = new(
            """
            DELETE FROM metadata.music_metadata WHERE media_guid = @media_guid
            """,
            """
            DELETE FROM metadata_music_metadata WHERE media_guid = @media_guid
            """),
        ["MetadataRepository.InsertMusicAsync.2"] = new(
            """
            INSERT INTO metadata.music_metadata
                (media_guid, album_title, album_type, disc_number, release_year, track_title, track_number, composer)
            VALUES
                (@media_guid, @album_title, @album_type, @disc_num, @release_year, @track_title, @track_num, @composer)
            """,
            """
            INSERT INTO metadata_music_metadata
                (media_guid, album_title, album_type, disc_number, release_year, track_title, track_number, composer)
            VALUES
                (@media_guid, @album_title, @album_type, @disc_num, @release_year, @track_title, @track_num, @composer)
            """),
    };
}
