using System.Data.Common;
using DataBridge.Persistence;

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

public sealed class MediaThumbnailGenerationService(ApplicationDatabase dataSource) : IMediaThumbnailGenerationService
{
    public async Task<IReadOnlyList<MissingMediaThumbnailItem>> ListMissingAsync(
        long accountId,
        Guid? afterMediaGuid,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = dataSource.Sql("MediaThumbnailGenerationService.ListMissingAsync.1");

        var items = new List<MissingMediaThumbnailItem>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("@account_id", accountId);
        command.Parameters.AddWithValue(
            "@after_media_guid",
            ApplicationParameterType.Guid,
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
        var sql = dataSource.Sql("MediaThumbnailGenerationService.CompleteAsync.1");

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);
        command.Parameters.AddWithValue("@storage_key", storageKey);
        command.Parameters.AddWithValue("@storage_path", storagePath);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
