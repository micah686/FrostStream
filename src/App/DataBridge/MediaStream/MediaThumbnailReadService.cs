using static DataBridge.NpgsqlDataReaderExtensions;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class MediaThumbnailReadService(NpgsqlDataSource dataSource) : IMediaThumbnailReadService
{
    public async Task<MediaThumbnailLocationDto?> ResolveAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                media_guid,
                storage_key,
                thumbnail_storage_path
            FROM metadata.media_metadata
            WHERE media_guid = @media_guid
              AND storage_key IS NOT NULL
              AND thumbnail_storage_path IS NOT NULL
            """);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new MediaThumbnailLocationDto
        {
            MediaGuid = GetGuid(reader, "media_guid"),
            StorageKey = GetString(reader, "storage_key"),
            StoragePath = GetString(reader, "thumbnail_storage_path")
        };
    }
}
