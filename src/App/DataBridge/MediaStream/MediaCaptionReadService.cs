using static DataBridge.NpgsqlDataReaderExtensions;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class MediaCaptionReadService(NpgsqlDataSource dataSource) : IMediaCaptionReadService
{
    public async Task<IReadOnlyList<MediaCaptionLocationDto>> ListAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT media_guid, storage_key, storage_path, two_digit_language_code, caption_type::text AS caption_type, name
            FROM metadata.media_captions
            WHERE media_guid = @media_guid AND storage_key IS NOT NULL
            ORDER BY two_digit_language_code, CASE WHEN caption_type::text = 'subtitles' THEN 0 ELSE 1 END, id
            """);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var items = new List<MediaCaptionLocationDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MediaCaptionLocationDto
            {
                MediaGuid = GetGuid(reader, "media_guid"),
                StorageKey = GetString(reader, "storage_key"),
                StoragePath = GetString(reader, "storage_path"),
                LanguageCode = GetString(reader, "two_digit_language_code"),
                CaptionType = GetString(reader, "caption_type"),
                Name = GetNullableString(reader, "name")
            });
        }

        return items;
    }

    public async Task<MediaCaptionLocationDto?> ResolveAsync(
        Guid mediaGuid,
        string languageCode,
        string? captionType,
        CancellationToken cancellationToken = default)
    {
        // Manual subtitles win over automatic captions when the caller does not pin a type.
        await using var command = dataSource.CreateCommand("""
            SELECT
                media_guid,
                storage_key,
                storage_path,
                two_digit_language_code,
                caption_type::text AS caption_type,
                name
            FROM metadata.media_captions
            WHERE media_guid = @media_guid
              AND two_digit_language_code = @language_code
              AND storage_key IS NOT NULL
              AND (@caption_type IS NULL OR caption_type::text = @caption_type)
            ORDER BY CASE WHEN caption_type::text = 'subtitles' THEN 0 ELSE 1 END
            LIMIT 1
            """);
        command.Parameters.AddWithValue("@media_guid", mediaGuid);
        command.Parameters.AddWithValue("@language_code", languageCode);
        command.Parameters.Add(new NpgsqlParameter("@caption_type", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)captionType ?? DBNull.Value
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new MediaCaptionLocationDto
        {
            MediaGuid = GetGuid(reader, "media_guid"),
            StorageKey = GetString(reader, "storage_key"),
            StoragePath = GetString(reader, "storage_path"),
            LanguageCode = GetString(reader, "two_digit_language_code"),
            CaptionType = GetString(reader, "caption_type"),
            Name = GetNullableString(reader, "name")
        };
    }
}
