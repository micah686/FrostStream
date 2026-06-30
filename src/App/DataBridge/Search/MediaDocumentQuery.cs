using Npgsql;
using static DataBridge.NpgsqlDataReaderExtensions;

namespace DataBridge.Search;

public sealed class MediaDocumentQuery(NpgsqlDataSource dataSource) : IMediaDocumentQuery
{
    public async Task<MediaDocument?> GetMediaByGuidAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = CreateMediaCommand("""
            WHERE mm.media_guid = @media_guid
            """);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var (items, _) = await ReadMediaDocumentsAsync(command, ct);
        return items.Count == 0 ? null : items[0];
    }

    public async Task<IReadOnlyList<MediaDocument>> GetMediaByGuidsAsync(IReadOnlyCollection<Guid> mediaGuids, CancellationToken ct = default)
    {
        if (mediaGuids.Count == 0)
            return [];

        await using var command = CreateMediaCommand("""
            WHERE mm.media_guid = ANY(@media_guids)
            ORDER BY mm.id
            """);
        command.Parameters.AddWithValue("@media_guids", mediaGuids.Distinct().ToArray());

        var (items, _) = await ReadMediaDocumentsAsync(command, ct);
        return items;
    }

    public async Task<IReadOnlyList<CommentDocument>> GetCommentsByMediaGuidAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = CreateCommentsCommand("""
            WHERE mc.media_guid = @media_guid
            ORDER BY mc.comment_timestamp, mc.id
            """);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var (items, _) = await ReadCommentDocumentsAsync(command, ct);
        return items;
    }

    public async Task<IReadOnlyList<CaptionDocument>> GetCaptionsByMediaGuidAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = CreateCaptionsCommand("""
            WHERE mc.media_guid = @media_guid
            ORDER BY mc.two_digit_language_code, mc.caption_type, mc.id
            """);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var (items, _) = await ReadCaptionDocumentsAsync(command, ct);
        return items;
    }

    public async Task<DocumentBatch<MediaDocument>> GetMediaBatchAsync(long lastId, int pageSize, CancellationToken ct = default)
    {
        await using var command = CreateMediaCommand("""
            WHERE mm.id > @last_id
            ORDER BY mm.id
            LIMIT @page_size
            """);
        command.Parameters.AddWithValue("@last_id", lastId);
        command.Parameters.AddWithValue("@page_size", Math.Max(1, pageSize));

        var (items, nextLastId) = await ReadMediaDocumentsAsync(command, ct);
        return new DocumentBatch<MediaDocument>(items, nextLastId == 0 ? lastId : nextLastId);
    }

    public async Task<DocumentBatch<CommentDocument>> GetCommentBatchAsync(long lastId, int pageSize, CancellationToken ct = default)
    {
        await using var command = CreateCommentsCommand("""
            WHERE mc.id > @last_id
            ORDER BY mc.id
            LIMIT @page_size
            """);
        command.Parameters.AddWithValue("@last_id", lastId);
        command.Parameters.AddWithValue("@page_size", Math.Max(1, pageSize));

        var (items, nextLastId) = await ReadCommentDocumentsAsync(command, ct);
        return new DocumentBatch<CommentDocument>(items, nextLastId == 0 ? lastId : nextLastId);
    }

    public async Task<DocumentBatch<CaptionDocument>> GetCaptionBatchAsync(long lastId, int pageSize, CancellationToken ct = default)
    {
        await using var command = CreateCaptionsCommand("""
            WHERE mc.id > @last_id
            ORDER BY mc.id
            LIMIT @page_size
            """);
        command.Parameters.AddWithValue("@last_id", lastId);
        command.Parameters.AddWithValue("@page_size", Math.Max(1, pageSize));

        var (items, nextLastId) = await ReadCaptionDocumentsAsync(command, ct);
        return new DocumentBatch<CaptionDocument>(items, nextLastId == 0 ? lastId : nextLastId);
    }

    private NpgsqlCommand CreateMediaCommand(string whereClause)
        => dataSource.CreateCommand($"""
            SELECT
                mm.id AS row_id,
                mm.media_guid,
                COALESCE(mm.title, '') AS title,
                mm.description,
                mm.thumbnail_storage_path,
                mm.webpage_url,
                EXTRACT(EPOCH FROM mm.release_date)::bigint AS release_date_unix,
                COALESCE(EXTRACT(EPOCH FROM mm.release_date)::bigint, 0) AS release_date_sort,
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
            {whereClause}
            """);

    private NpgsqlCommand CreateCommentsCommand(string whereClause)
        => dataSource.CreateCommand($"""
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
            {whereClause}
            """);

    private NpgsqlCommand CreateCaptionsCommand(string whereClause)
        => dataSource.CreateCommand($"""
            SELECT
                mc.id AS row_id,
                mc.media_guid::text || ':' || mc.two_digit_language_code || ':' || mc.caption_type::text AS id,
                mc.media_guid,
                mc.two_digit_language_code AS language_code,
                mc.caption_type::text AS caption_type,
                mc.name,
                mc.storage_path,
                mc.text_content
            FROM metadata.media_captions mc
            JOIN media.media m ON m.media_guid = mc.media_guid
            {whereClause}
            """);

    private static async Task<(IReadOnlyList<MediaDocument> Documents, long LastId)> ReadMediaDocumentsAsync(
        NpgsqlCommand command,
        CancellationToken ct)
    {
        var documents = new List<MediaDocument>();
        var lastId = 0L;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lastId = GetInt64(reader, "row_id");
            var mediaGuid = GetGuid(reader, "media_guid");
            documents.Add(new MediaDocument
            {
                Id = mediaGuid.ToString("N"),
                Title = GetString(reader, "title"),
                Description = GetNullableString(reader, "description"),
                ThumbnailStoragePath = GetNullableString(reader, "thumbnail_storage_path"),
                AccountAvatarStoragePath = GetNullableString(reader, "account_avatar_storage_path"),
                WebpageUrl = GetNullableString(reader, "webpage_url"),
                ReleaseDateUnix = GetNullableInt64(reader, "release_date_unix"),
                ReleaseDateSort = GetInt64(reader, "release_date_sort"),
                ViewCount = GetNullableInt64(reader, "view_count"),
                LikeCount = GetNullableInt64(reader, "like_count"),
                DurationSeconds = GetNullableDouble(reader, "duration"),
                WasLive = GetBoolean(reader, "was_live"),
                Availability = GetNullableString(reader, "availability"),
                AgeLimit = GetNullableInt32(reader, "age_limit"),
                VideoCodec = GetNullableString(reader, "video_codec"),
                AudioCodec = GetNullableString(reader, "audio_codec"),
                VideoWidth = GetNullableInt32(reader, "video_width"),
                VideoHeight = GetNullableInt32(reader, "video_height"),
                ResolutionLabel = GetNullableString(reader, "resolution_label"),
                HdrType = GetNullableString(reader, "hdr_type"),
                AudioChannels = GetNullableInt32(reader, "audio_channels"),
                Platform = GetString(reader, "platform"),
                AccountId = GetInt64(reader, "account_id"),
                AccountName = GetString(reader, "account_name"),
                AccountHandle = GetString(reader, "account_handle"),
                Tags = GetJsonList<string>(reader, "tags_json"),
                Categories = GetJsonList<string>(reader, "categories_json"),
                Genres = GetJsonList<string>(reader, "genres_json"),
                Artists = GetJsonList<string>(reader, "artists_json"),
                CaptionLanguages = GetJsonList<string>(reader, "caption_languages_json")
            });
        }

        return (documents, lastId);
    }

    private static async Task<(IReadOnlyList<CommentDocument> Documents, long LastId)> ReadCommentDocumentsAsync(
        NpgsqlCommand command,
        CancellationToken ct)
    {
        var documents = new List<CommentDocument>();
        var lastId = 0L;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lastId = GetInt64(reader, "row_id");
            documents.Add(new CommentDocument
            {
                Id = GetString(reader, "id"),
                MediaGuid = GetGuid(reader, "media_guid").ToString("N"),
                ParentCommentId = GetString(reader, "parent_comment_id"),
                Text = GetString(reader, "text"),
                CommentTimestampUnix = GetInt64(reader, "comment_timestamp_unix"),
                LikeCount = GetNullableInt32(reader, "like_count"),
                DislikeCount = GetNullableInt32(reader, "dislike_count"),
                IsFavorited = GetBoolean(reader, "is_favorited"),
                IsPinned = GetBoolean(reader, "is_pinned"),
                IsUploader = GetBoolean(reader, "is_uploader"),
                AccountId = GetInt64(reader, "account_id"),
                AccountName = GetString(reader, "account_name"),
                AccountHandle = GetString(reader, "account_handle"),
                Platform = GetString(reader, "platform"),
                AccountAvatarStoragePath = GetNullableString(reader, "account_avatar_storage_path")
            });
        }

        return (documents, lastId);
    }

    private static async Task<(IReadOnlyList<CaptionDocument> Documents, long LastId)> ReadCaptionDocumentsAsync(
        NpgsqlCommand command,
        CancellationToken ct)
    {
        var documents = new List<CaptionDocument>();
        var lastId = 0L;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lastId = GetInt64(reader, "row_id");
            documents.Add(new CaptionDocument
            {
                Id = GetString(reader, "id"),
                MediaGuid = GetGuid(reader, "media_guid").ToString("N"),
                LanguageCode = GetString(reader, "language_code"),
                CaptionType = GetString(reader, "caption_type"),
                Name = GetNullableString(reader, "name"),
                StoragePath = GetString(reader, "storage_path"),
                Text = GetNullableString(reader, "text_content")
            });
        }

        return (documents, lastId);
    }

}
