using System.Data.Common;
using DataBridge.Persistence;
using static DataBridge.ApplicationDataReaderExtensions;

namespace DataBridge.Search;

public sealed class MediaDocumentQuery(ApplicationDatabase dataSource) : IMediaDocumentQuery
{
    public async Task<MediaDocument?> GetMediaByGuidAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = CreateMediaCommand(dataSource.Sql("MediaDocumentQuery.GetMediaByGuidAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var (items, _) = await ReadMediaDocumentsAsync(command, ct);
        return items.Count == 0 ? null : items[0];
    }

    public async Task<IReadOnlyList<MediaDocument>> GetMediaByGuidsAsync(IReadOnlyCollection<Guid> mediaGuids, CancellationToken ct = default)
    {
        if (mediaGuids.Count == 0)
            return [];

        await using var command = CreateMediaCommand(dataSource.Sql("MediaDocumentQuery.GetMediaByGuidsAsync.1"));
        command.Parameters.AddWithValue("@media_guids", mediaGuids.Distinct().ToArray());

        var (items, _) = await ReadMediaDocumentsAsync(command, ct);
        return items;
    }

    public async Task<IReadOnlyList<CommentDocument>> GetCommentsByMediaGuidAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = CreateCommentsCommand(dataSource.Sql("MediaDocumentQuery.GetCommentsByMediaGuidAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var (items, _) = await ReadCommentDocumentsAsync(command, ct);
        return items;
    }

    public async Task<IReadOnlyList<CaptionDocument>> GetCaptionsByMediaGuidAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = CreateCaptionsCommand(dataSource.Sql("MediaDocumentQuery.GetCaptionsByMediaGuidAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var (items, _) = await ReadCaptionDocumentsAsync(command, ct);
        return items;
    }

    public async Task<DocumentBatch<MediaDocument>> GetMediaBatchAsync(long lastId, int pageSize, CancellationToken ct = default)
    {
        await using var command = CreateMediaCommand(dataSource.Sql("MediaDocumentQuery.GetMediaBatchAsync.1"));
        command.Parameters.AddWithValue("@last_id", lastId);
        command.Parameters.AddWithValue("@page_size", Math.Max(1, pageSize));

        var (items, nextLastId) = await ReadMediaDocumentsAsync(command, ct);
        return new DocumentBatch<MediaDocument>(items, nextLastId == 0 ? lastId : nextLastId);
    }

    public async Task<DocumentBatch<CommentDocument>> GetCommentBatchAsync(long lastId, int pageSize, CancellationToken ct = default)
    {
        await using var command = CreateCommentsCommand(dataSource.Sql("MediaDocumentQuery.GetCommentBatchAsync.1"));
        command.Parameters.AddWithValue("@last_id", lastId);
        command.Parameters.AddWithValue("@page_size", Math.Max(1, pageSize));

        var (items, nextLastId) = await ReadCommentDocumentsAsync(command, ct);
        return new DocumentBatch<CommentDocument>(items, nextLastId == 0 ? lastId : nextLastId);
    }

    public async Task<DocumentBatch<CaptionDocument>> GetCaptionBatchAsync(long lastId, int pageSize, CancellationToken ct = default)
    {
        await using var command = CreateCaptionsCommand(dataSource.Sql("MediaDocumentQuery.GetCaptionBatchAsync.1"));
        command.Parameters.AddWithValue("@last_id", lastId);
        command.Parameters.AddWithValue("@page_size", Math.Max(1, pageSize));

        var (items, nextLastId) = await ReadCaptionDocumentsAsync(command, ct);
        return new DocumentBatch<CaptionDocument>(items, nextLastId == 0 ? lastId : nextLastId);
    }

    private DbCommand CreateMediaCommand(string whereClause)
        => dataSource.CreateCommand(dataSource.Sql("MediaDocumentQuery.CreateMediaCommand.1", whereClause));

    private DbCommand CreateCommentsCommand(string whereClause)
        => dataSource.CreateCommand(dataSource.Sql("MediaDocumentQuery.CreateCommentsCommand.1", whereClause));

    private DbCommand CreateCaptionsCommand(string whereClause)
        => dataSource.CreateCommand(dataSource.Sql("MediaDocumentQuery.CreateCaptionsCommand.1", whereClause));

    private static async Task<(IReadOnlyList<MediaDocument> Documents, long LastId)> ReadMediaDocumentsAsync(
        DbCommand command,
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
                AddedAtSort = GetInt64(reader, "added_at_sort"),
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
        DbCommand command,
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
        DbCommand command,
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
                StorageKey = GetString(reader, "storage_key")
            });
        }

        return (documents, lastId);
    }

}
