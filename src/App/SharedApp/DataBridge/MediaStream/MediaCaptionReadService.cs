using static DataBridge.ApplicationDataReaderExtensions;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class MediaCaptionReadService(ApplicationDatabase dataSource) : IMediaCaptionReadService
{
    public async Task<IReadOnlyList<MediaCaptionLocationDto>> ListAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MediaCaptionReadService.ListAsync.1"));
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
        await using var command = dataSource.CreateCommand(dataSource.Sql("MediaCaptionReadService.ResolveAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);
        command.Parameters.AddWithValue("@language_code", languageCode);
        command.Parameters.AddWithValue("@caption_type", ApplicationParameterType.Text, captionType);

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
