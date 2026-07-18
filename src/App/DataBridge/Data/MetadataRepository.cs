using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using Shared.Metadata;

namespace DataBridge.Data;

public sealed class MetadataRepository(DataBridgeDbContext db) : IMetadataRepository
{
    public async Task UpsertAccountAssetsAsync(
        string platform,
        string accountHandle,
        string accountName,
        string? accountUrl,
        string? avatarStoragePath,
        string? bannerStoragePath,
        string storageKey,
        CancellationToken ct = default)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened)
            await conn.OpenAsync(ct);

        try
        {
            // Don't overwrite account_name on conflict: a media-download write may already have
            // recorded a better name than a channel refresh can derive.
            await using var cmd = new NpgsqlCommand("""
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
                """, conn);

            cmd.Parameters.AddWithValue("@platform", platform);
            cmd.Parameters.AddWithValue("@account_name", accountName);
            cmd.Parameters.AddWithValue("@account_handle", accountHandle);
            cmd.Parameters.AddWithValue("@account_url", (object?)accountUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@avatar_path", (object?)avatarStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@banner_path", (object?)bannerStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@storage_key", (object?)storageKey ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (opened)
                await conn.CloseAsync();
        }
    }

    public async Task<bool> UpdateAccountAssetsByIdAsync(
        long accountId,
        string? avatarStoragePath,
        string? bannerStoragePath,
        string? storageKey,
        CancellationToken ct = default)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened)
            await conn.OpenAsync(ct);

        try
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE metadata.accounts SET
                    avatar_storage_path = COALESCE(@avatar_path, avatar_storage_path),
                    banner_storage_path = COALESCE(@banner_path, banner_storage_path),
                    storage_key         = COALESCE(@storage_key, storage_key)
                WHERE id = @account_id
                """, conn);

            cmd.Parameters.AddWithValue("@account_id", accountId);
            cmd.Parameters.AddWithValue("@avatar_path", (object?)avatarStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@banner_path", (object?)bannerStoragePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@storage_key", (object?)storageKey ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            if (opened)
                await conn.CloseAsync();
        }
    }

    public async Task WriteMetadataAsync(Guid mediaGuid, CapturedMediaMetadata metadata, string storageKey, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTx = (NpgsqlTransaction)tx.GetDbTransaction();

        try
        {
            var accountId = await UpsertAccountAsync(conn, npgsqlTx, metadata.Account, ct);
            var mediaMetadataId = await InsertMediaMetadataAsync(conn, npgsqlTx, mediaGuid, accountId, metadata.Media, storageKey, ct);
            await InsertTaxonomyAsync(conn, npgsqlTx, mediaMetadataId, metadata, ct);
            await InsertTechnicalAsync(conn, npgsqlTx, mediaGuid, metadata.Technical, ct);
            await InsertCaptionsAsync(conn, npgsqlTx, mediaGuid, metadata.Captions, storageKey, ct);
            await InsertCommentsAsync(conn, npgsqlTx, mediaGuid, metadata.Comments, ct);
            await InsertSeriesAsync(conn, npgsqlTx, mediaGuid, metadata.Series, ct);
            await InsertMusicAsync(conn, npgsqlTx, mediaGuid, metadata.Music, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── accounts ────────────────────────────────────────────────────────────

    private static async Task<long> UpsertAccountAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CapturedAccountMetadata account,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO metadata.accounts
                (platform, account_name, account_handle, account_url, account_follower_count, is_verified, account_description)
            VALUES
                (@platform, @account_name, @account_handle, @account_url, @follower_count, false, @description)
            ON CONFLICT (platform, account_handle) DO UPDATE SET
                account_name       = EXCLUDED.account_name,
                account_url        = EXCLUDED.account_url,
                account_follower_count = EXCLUDED.account_follower_count,
                account_description = EXCLUDED.account_description
            RETURNING id
            """, conn, tx);

        cmd.Parameters.AddWithValue("@platform", account.Platform);
        cmd.Parameters.AddWithValue("@account_name", account.AccountName);
        cmd.Parameters.AddWithValue("@account_handle", account.AccountHandle);
        cmd.Parameters.AddWithValue("@account_url", (object?)account.AccountUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@follower_count", (object?)account.FollowerCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description", (object?)account.Description ?? DBNull.Value);

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    // ── media_metadata ───────────────────────────────────────────────────────

    private static async Task<long> InsertMediaMetadataAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid mediaGuid,
        long accountId,
        CapturedMediaMetadataCore core,
        string storageKey,
        CancellationToken ct)
    {
        await using var del = new NpgsqlCommand(
            "DELETE FROM metadata.media_metadata WHERE media_guid = @media_guid", conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        await using var ins = new NpgsqlCommand("""
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
            """, conn, tx);

        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@account_id", accountId);
        ins.Parameters.AddWithValue("@external_media_id", (object?)core.ExternalMediaId ?? DBNull.Value);
        ins.Parameters.AddWithValue("@scrape_date", core.MetadataScrapeDate.ToDateTimeUtc());
        ins.Parameters.AddWithValue("@thumbnail", (object?)core.ThumbnailStoragePath ?? DBNull.Value);
        ins.Parameters.AddWithValue("@storage_key", (object?)(core.ThumbnailStoragePath is null ? null : storageKey) ?? DBNull.Value);
        ins.Parameters.AddWithValue("@age_limit", (object?)core.AgeLimit ?? DBNull.Value);
        ins.Parameters.AddWithValue("@avg_rating", (object?)core.AverageRating ?? DBNull.Value);
        ins.Parameters.AddWithValue("@like_count", (object?)core.LikeCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@dislike_count", (object?)core.DislikeCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@duration", (object?)core.DurationSeconds ?? DBNull.Value);
        ins.Parameters.AddWithValue("@description", (object?)core.Description ?? DBNull.Value);
        ins.Parameters.AddWithValue("@release_date", core.ReleaseDate.HasValue ? (object)core.ReleaseDate.Value.ToDateTimeUtc() : DBNull.Value);
        ins.Parameters.AddWithValue("@title", (object?)core.Title ?? DBNull.Value);
        ins.Parameters.AddWithValue("@was_live", core.WasLive);
        ins.Parameters.AddWithValue("@webpage_url", (object?)core.WebpageUrl ?? DBNull.Value);
        ins.Parameters.AddWithValue("@view_count", (object?)core.ViewCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@comment_count", (object?)core.CommentCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@availability", core.Availability ?? "");
        ins.Parameters.AddWithValue("@location", (object?)core.Location ?? DBNull.Value);

        return Convert.ToInt64(await ins.ExecuteScalarAsync(ct));
    }

    // ── taxonomy (artists, genres, tags, categories, cast) ──────────────────

    private static async Task InsertTaxonomyAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long mediaMetadataId,
        CapturedMediaMetadata metadata,
        CancellationToken ct)
    {
        foreach (var artist in metadata.Artists)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.artists", "artist_name", artist, ct);
            await InsertJunctionAsync(conn, tx,
                "INSERT INTO metadata.media_artists (media_metadata_id, artist_id, is_album_artist) VALUES (@mm, @ref, false) ON CONFLICT DO NOTHING",
                mediaMetadataId, id, ct);
        }

        foreach (var artist in metadata.AlbumArtists)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.artists", "artist_name", artist, ct);
            await InsertJunctionAsync(conn, tx,
                "INSERT INTO metadata.media_artists (media_metadata_id, artist_id, is_album_artist) VALUES (@mm, @ref, true) ON CONFLICT DO NOTHING",
                mediaMetadataId, id, ct);
        }

        foreach (var genre in metadata.Genres)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.genres", "genre_name", genre, ct);
            await InsertJunctionAsync(conn, tx,
                "INSERT INTO metadata.media_genres (media_metadata_id, genre_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING",
                mediaMetadataId, id, ct);
        }

        foreach (var tag in metadata.Tags)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.tags", "tag_name", tag, ct);
            await InsertJunctionAsync(conn, tx,
                "INSERT INTO metadata.media_tags (media_metadata_id, tag_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING",
                mediaMetadataId, id, ct);
        }

        foreach (var category in metadata.Categories)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.categories", "category_name", category, ct);
            await InsertJunctionAsync(conn, tx,
                "INSERT INTO metadata.media_categories (media_metadata_id, category_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING",
                mediaMetadataId, id, ct);
        }

        foreach (var cast in metadata.Cast)
        {
            var id = await UpsertLookupAsync(conn, tx, "metadata.cast_members", "cast_name", cast, ct);
            await InsertJunctionAsync(conn, tx,
                "INSERT INTO metadata.media_cast (media_metadata_id, cast_member_id) VALUES (@mm, @ref) ON CONFLICT DO NOTHING",
                mediaMetadataId, id, ct);
        }
    }

    private static async Task<long> UpsertLookupAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string table,
        string column,
        string value,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {table} ({column}) VALUES (@v) ON CONFLICT ({column}) DO UPDATE SET {column} = EXCLUDED.{column} RETURNING id",
            conn, tx);
        cmd.Parameters.AddWithValue("@v", value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertJunctionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string sql,
        long mediaMetadataId,
        long refId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@mm", mediaMetadataId);
        cmd.Parameters.AddWithValue("@ref", refId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── technical (media_base, format, streams, chapters) ───────────────────

    private static async Task InsertTechnicalAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid mediaGuid,
        CapturedMediaTechnicalMetadata technical,
        CancellationToken ct)
    {
        await using var del = new NpgsqlCommand(
            "DELETE FROM metadata.media_base WHERE media_guid = @media_guid", conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        await using var ins = new NpgsqlCommand(
            "INSERT INTO metadata.media_base (media_guid, duration_ticks) VALUES (@media_guid, @ticks) RETURNING id",
            conn, tx);
        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@ticks", technical.DurationTicks);
        var mediaBaseId = Convert.ToInt64(await ins.ExecuteScalarAsync(ct));

        var fmt = technical.Format;
        await using var fmtCmd = new NpgsqlCommand("""
            INSERT INTO metadata.media_format_details
                (media_base_id, duration_ticks, start_time_ticks, format_long_names, stream_count, bit_rate)
            VALUES (@base_id, @dur, @start, @names, @streams, @bitrate)
            """, conn, tx);
        fmtCmd.Parameters.AddWithValue("@base_id", mediaBaseId);
        fmtCmd.Parameters.AddWithValue("@dur", fmt.DurationTicks);
        fmtCmd.Parameters.AddWithValue("@start", fmt.StartTimeTicks);
        fmtCmd.Parameters.AddWithValue("@names", fmt.FormatLongNames);
        fmtCmd.Parameters.AddWithValue("@streams", fmt.StreamCount);
        fmtCmd.Parameters.AddWithValue("@bitrate", fmt.BitRate);
        await fmtCmd.ExecuteNonQueryAsync(ct);

        foreach (var stream in technical.Streams)
        {
            await using var streamCmd = new NpgsqlCommand("""
                INSERT INTO metadata.media_streams
                    (media_base_id, stream_type, is_primary, codec_name, codec_long_name,
                     bit_rate, bit_depth, start_time_ticks, duration_ticks, language)
                VALUES (@base_id, @type, @primary, @codec, @codec_long,
                        @bitrate, @depth, 0, @dur, @lang)
                RETURNING id
                """, conn, tx);
            streamCmd.Parameters.AddWithValue("@base_id", mediaBaseId);
            streamCmd.Parameters.AddWithValue("@type", stream.StreamType);
            streamCmd.Parameters.AddWithValue("@primary", stream.IsPrimary);
            streamCmd.Parameters.AddWithValue("@codec", stream.CodecName);
            streamCmd.Parameters.AddWithValue("@codec_long", stream.CodecLongName);
            streamCmd.Parameters.AddWithValue("@bitrate", stream.BitRate);
            streamCmd.Parameters.AddWithValue("@depth", (object?)stream.BitDepth ?? DBNull.Value);
            streamCmd.Parameters.AddWithValue("@dur", stream.DurationTicks);
            streamCmd.Parameters.AddWithValue("@lang", (object?)stream.Language ?? DBNull.Value);
            var streamId = Convert.ToInt64(await streamCmd.ExecuteScalarAsync(ct));

            if (stream.Video is { } video)
            {
                await using var vidCmd = new NpgsqlCommand("""
                    INSERT INTO metadata.video_stream_details
                        (media_stream_id, avg_frame_rate, bits_per_raw_sample,
                         display_aspect_ratio_width, display_aspect_ratio_height,
                         profile, width, height, pixel_format, rotation,
                         color_space, color_transfer, color_primaries, hdr_type)
                    VALUES (@id, @fps, 0, 0, 0, '', @w, @h, '', 0, '', '', '', @hdr)
                    """, conn, tx);
                vidCmd.Parameters.AddWithValue("@id", streamId);
                vidCmd.Parameters.AddWithValue("@fps", video.AvgFrameRate);
                vidCmd.Parameters.AddWithValue("@w", video.Width);
                vidCmd.Parameters.AddWithValue("@h", video.Height);
                vidCmd.Parameters.AddWithValue("@hdr", video.HdrType);
                await vidCmd.ExecuteNonQueryAsync(ct);
            }

            if (stream.Audio is { } audio)
            {
                await using var audCmd = new NpgsqlCommand("""
                    INSERT INTO metadata.audio_stream_details
                        (media_stream_id, channels, channel_layout, sample_rate_hz, profile)
                    VALUES (@id, @channels, '', @rate, '')
                    """, conn, tx);
                audCmd.Parameters.AddWithValue("@id", streamId);
                audCmd.Parameters.AddWithValue("@channels", audio.Channels);
                audCmd.Parameters.AddWithValue("@rate", audio.SampleRateHz);
                await audCmd.ExecuteNonQueryAsync(ct);
            }
        }

        foreach (var chapter in technical.Chapters)
        {
            await using var chapCmd = new NpgsqlCommand("""
                INSERT INTO metadata.chapter_data (media_base_id, title, start_ticks, end_ticks)
                VALUES (@base_id, @title, @start, @end)
                """, conn, tx);
            chapCmd.Parameters.AddWithValue("@base_id", mediaBaseId);
            chapCmd.Parameters.AddWithValue("@title", chapter.Title);
            chapCmd.Parameters.AddWithValue("@start", chapter.StartTicks);
            chapCmd.Parameters.AddWithValue("@end", chapter.EndTicks);
            await chapCmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ── captions ─────────────────────────────────────────────────────────────

    private static async Task InsertCaptionsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid mediaGuid,
        IReadOnlyList<CapturedCaptionMetadata> captions,
        string storageKey,
        CancellationToken ct)
    {
        await using var del = new NpgsqlCommand(
            "DELETE FROM metadata.media_captions WHERE media_guid = @media_guid", conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        foreach (var caption in captions)
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO metadata.media_captions
                    (media_guid, storage_path, storage_key, caption_type, two_digit_language_code, name)
                VALUES
                    (@media_guid, @path, @storage_key, @type::metadata.subtitle_type_enum, @lang, @name)
                """, conn, tx);
            ins.Parameters.AddWithValue("@media_guid", mediaGuid);
            ins.Parameters.AddWithValue("@path", caption.StoragePath);
            ins.Parameters.AddWithValue("@storage_key", storageKey);
            ins.Parameters.AddWithValue("@type", caption.CaptionType);
            ins.Parameters.AddWithValue("@lang", caption.LanguageCode);
            ins.Parameters.AddWithValue("@name", (object?)caption.Name ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    // ── comments ─────────────────────────────────────────────────────────────

    private static async Task InsertCommentsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid mediaGuid,
        IReadOnlyList<CapturedCommentMetadata> comments,
        CancellationToken ct)
    {
        await using var del = new NpgsqlCommand(
            "DELETE FROM metadata.media_comments WHERE media_guid = @media_guid", conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        foreach (var comment in comments)
        {
            var commentAccountId = await UpsertAccountAsync(conn, tx, comment.Account, ct);

            await using var ins = new NpgsqlCommand("""
                INSERT INTO metadata.media_comments
                    (media_guid, comment_id, parent_comment_id, text_comment, account_id,
                     comment_timestamp, like_count, dislike_count, is_favorited, is_uploader, is_pinned)
                VALUES
                    (@media_guid, @comment_id, @parent_id, @text, @account_id,
                     @timestamp, @like_count, @dislike_count, @is_favorited, @is_uploader, @is_pinned)
                """, conn, tx);
            ins.Parameters.AddWithValue("@media_guid", mediaGuid);
            ins.Parameters.AddWithValue("@comment_id", comment.CommentId);
            ins.Parameters.AddWithValue("@parent_id", (object?)comment.ParentCommentId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@text", comment.Text);
            ins.Parameters.AddWithValue("@account_id", commentAccountId);
            ins.Parameters.AddWithValue("@timestamp", comment.CommentTimestamp.ToDateTimeUtc());
            ins.Parameters.AddWithValue("@like_count", (object?)comment.LikeCount ?? DBNull.Value);
            ins.Parameters.AddWithValue("@dislike_count", (object?)comment.DislikeCount ?? DBNull.Value);
            ins.Parameters.AddWithValue("@is_favorited", comment.IsFavorited);
            ins.Parameters.AddWithValue("@is_uploader", comment.IsUploader);
            ins.Parameters.AddWithValue("@is_pinned", comment.IsPinned);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    // ── series ────────────────────────────────────────────────────────────────

    private static async Task InsertSeriesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid mediaGuid,
        CapturedSeriesMetadata? series,
        CancellationToken ct)
    {
        await using var del = new NpgsqlCommand(
            "DELETE FROM metadata.series_metadata WHERE media_guid = @media_guid", conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        if (series is null)
            return;

        await using var ins = new NpgsqlCommand("""
            INSERT INTO metadata.series_metadata
                (media_guid, series_name, season_count, season_number, season_name, episode_number, episode_name)
            VALUES
                (@media_guid, @series_name, @season_count, @season_num, @season_name, @episode_num, @episode_name)
            """, conn, tx);
        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@series_name", series.SeriesName);
        ins.Parameters.AddWithValue("@season_count", (object?)series.SeasonCount ?? DBNull.Value);
        ins.Parameters.AddWithValue("@season_num", series.SeasonNumber);
        ins.Parameters.AddWithValue("@season_name", (object?)series.SeasonName ?? DBNull.Value);
        ins.Parameters.AddWithValue("@episode_num", series.EpisodeNumber);
        ins.Parameters.AddWithValue("@episode_name", series.EpisodeName);
        await ins.ExecuteNonQueryAsync(ct);
    }

    // ── music ─────────────────────────────────────────────────────────────────

    private static async Task InsertMusicAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid mediaGuid,
        CapturedMusicMetadata? music,
        CancellationToken ct)
    {
        await using var del = new NpgsqlCommand(
            "DELETE FROM metadata.music_metadata WHERE media_guid = @media_guid", conn, tx);
        del.Parameters.AddWithValue("@media_guid", mediaGuid);
        await del.ExecuteNonQueryAsync(ct);

        if (music is null)
            return;

        await using var ins = new NpgsqlCommand("""
            INSERT INTO metadata.music_metadata
                (media_guid, album_title, album_type, disc_number, release_year, track_title, track_number, composer)
            VALUES
                (@media_guid, @album_title, @album_type, @disc_num, @release_year, @track_title, @track_num, @composer)
            """, conn, tx);
        ins.Parameters.AddWithValue("@media_guid", mediaGuid);
        ins.Parameters.AddWithValue("@album_title", music.AlbumTitle);
        ins.Parameters.AddWithValue("@album_type", (object?)music.AlbumType ?? DBNull.Value);
        ins.Parameters.AddWithValue("@disc_num", (object?)music.DiscNumber ?? DBNull.Value);
        ins.Parameters.AddWithValue("@release_year", (object?)music.ReleaseYear ?? DBNull.Value);
        ins.Parameters.AddWithValue("@track_title", music.TrackTitle);
        ins.Parameters.AddWithValue("@track_num", music.TrackNumber);
        ins.Parameters.AddWithValue("@composer", (object?)music.Composer ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync(ct);
    }
}
