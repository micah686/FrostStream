using static DataBridge.ApplicationDataReaderExtensions;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class MediaThumbnailReadService(ApplicationDatabase dataSource) : IMediaThumbnailReadService
{
    public async Task<MediaThumbnailLocationDto?> ResolveAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MediaThumbnailReadService.ResolveAsync.1"));
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
