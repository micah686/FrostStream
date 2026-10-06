using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.AudioRenditions;

/// <summary>
/// Durable, job-independent "is this media's audio encoded" fact table
/// (<c>media.audio_encoding_status</c>). Separate from <see cref="AudioRenditionRepository"/>, which is
/// job/queue-shaped; this stays a stable read/write contract even if rendition or job history is purged.
/// </summary>
public sealed class MediaEncodingStatusRepository(
    DataBridgeDbContext db,
    ApplicationDatabase dataSource,
    IClock clock) : IMediaEncodingStatusRepository
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<MediaEncodingStatusDto?> SetAsync(
        long accountId,
        Guid mediaGuid,
        bool isEncoded,
        string? storageKey,
        string? storagePath,
        CancellationToken cancellationToken = default)
    {
        if (!await MediaBelongsToAccountAsync(accountId, mediaGuid, cancellationToken))
            return null;

        return await UpsertAsync(accountId, mediaGuid, isEncoded, storageKey, storagePath, cancellationToken);
    }

    public async Task<MediaEncodingStatusDto?> SetByMediaGuidAsync(
        Guid mediaGuid,
        bool isEncoded,
        string? storageKey,
        string? storagePath,
        CancellationToken cancellationToken = default)
    {
        var accountId = await ReadAccountIdForMediaAsync(mediaGuid, cancellationToken);
        if (accountId is null)
            return null;

        return await UpsertAsync(accountId.Value, mediaGuid, isEncoded, storageKey, storagePath, cancellationToken);
    }

    private async Task<MediaEncodingStatusDto> UpsertAsync(
        long accountId,
        Guid mediaGuid,
        bool isEncoded,
        string? storageKey,
        string? storagePath,
        CancellationToken cancellationToken)
    {
        var entity = await db.MediaEncodingStatuses
            .FirstOrDefaultAsync(x => x.MediaGuid == mediaGuid, cancellationToken);
        var now = clock.GetCurrentInstant();

        if (entity is null)
        {
            entity = new MediaEncodingStatusEntity
            {
                MediaGuid = mediaGuid,
                AccountId = accountId,
                IsEncoded = isEncoded,
                StorageKey = storageKey,
                StoragePath = storagePath,
                EncodedAt = isEncoded ? now : null,
                UpdatedAt = now
            };
            db.MediaEncodingStatuses.Add(entity);
        }
        else
        {
            entity.IsEncoded = isEncoded;
            entity.StorageKey = storageKey ?? entity.StorageKey;
            entity.StoragePath = storagePath ?? entity.StoragePath;
            entity.EncodedAt = isEncoded ? entity.EncodedAt ?? now : null;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<ChannelEncodingStatusPage> ListChannelAsync(
        long accountId,
        bool? isEncodedFilter,
        string? storageKey,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var offset = DecodeCursor(cursor);
        storageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey;

        // Filters against the media's archived source storage key (media_content_id_versions), the
        // same thing /status filters on, not audio_encoding_status.storage_key — items that aren't
        // encoded yet have no row there, so filtering by that column would silently drop them.
        await using var encodedCountCommand = dataSource.CreateCommand(dataSource.Sql("MediaEncodingStatusRepository.ListChannelAsync.1"));
        encodedCountCommand.Parameters.AddWithValue("@account_id", accountId);
        encodedCountCommand.Parameters.AddWithValue("@storage_key", (object?)storageKey ?? DBNull.Value);
        var encodedCount = (int)(long)(await encodedCountCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using var command = dataSource.CreateCommand(dataSource.Sql("MediaEncodingStatusRepository.ListChannelAsync.2"));
        command.Parameters.AddWithValue("@account_id", accountId);
        command.Parameters.AddWithValue("@storage_key", (object?)storageKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@is_encoded", (object?)isEncodedFilter ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);

        var items = new List<ChannelEncodedMediaItemDto>();
        var totalCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ChannelEncodedMediaItemDto
            {
                MediaGuid = reader.GetGuid(0),
                Title = reader.GetString(1),
                IsEncoded = reader.GetBoolean(2),
                StorageKey = reader.IsDBNull(3) ? null : reader.GetString(3),
                StoragePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                EncodedAt = reader.IsDBNull(5) ? null : Instant.FromUnixTimeSeconds(reader.GetInt64(5))
            });
            totalCount = (int)reader.GetInt64(6);
        }

        var nextOffset = offset + items.Count;
        var nextCursor = nextOffset < totalCount ? EncodeCursor(nextOffset) : null;

        return new ChannelEncodingStatusPage(items, nextCursor, totalCount, encodedCount);
    }

    private async Task<bool> MediaBelongsToAccountAsync(long accountId, Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MediaEncodingStatusRepository.MediaBelongsToAccountAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);
        command.Parameters.AddWithValue("@account_id", accountId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private async Task<long?> ReadAccountIdForMediaAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MediaEncodingStatusRepository.ReadAccountIdForMediaAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (long)result;
    }

    private static MediaEncodingStatusDto ToDto(MediaEncodingStatusEntity entity)
        => new()
        {
            MediaGuid = entity.MediaGuid,
            AccountId = entity.AccountId,
            IsEncoded = entity.IsEncoded,
            StorageKey = entity.StorageKey,
            StoragePath = entity.StoragePath,
            EncodedAt = entity.EncodedAt,
            UpdatedAt = entity.UpdatedAt
        };

    private static string EncodeCursor(int offset)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset >= 0
                ? offset
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}
