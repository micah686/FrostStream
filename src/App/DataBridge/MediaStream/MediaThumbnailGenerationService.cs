using Npgsql;
using NpgsqlTypes;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public interface IMediaThumbnailGenerationService
{
    Task<IReadOnlyList<MissingMediaThumbnailItem>> ListMissingAsync(
        long accountId,
        Guid? afterMediaGuid,
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid mediaGuid,
        string storageKey,
        string storagePath,
        CancellationToken cancellationToken = default);
}

public sealed class MediaThumbnailGenerationService(NpgsqlDataSource dataSource) : IMediaThumbnailGenerationService
{
    public async Task<IReadOnlyList<MissingMediaThumbnailItem>> ListMissingAsync(
        long accountId,
        Guid? afterMediaGuid,
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
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
            """;

        var items = new List<MissingMediaThumbnailItem>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("@account_id", accountId);
        command.Parameters.AddWithValue(
            "@after_media_guid",
            NpgsqlDbType.Uuid,
            (object?)afterMediaGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MissingMediaThumbnailItem
            {
                MediaGuid = reader.GetGuid(0),
                StorageKey = reader.GetString(1),
                StoragePath = reader.GetString(2)
            });
        }

        return items;
    }

    public async Task<bool> CompleteAsync(
        Guid mediaGuid,
        string storageKey,
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE metadata.media_metadata
            SET thumbnail_storage_path = @storage_path,
                storage_key = @storage_key
            WHERE media_guid = @media_guid
              AND NULLIF(BTRIM(thumbnail_storage_path), '') IS NULL
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);
        command.Parameters.AddWithValue("@storage_key", storageKey);
        command.Parameters.AddWithValue("@storage_path", storagePath);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
