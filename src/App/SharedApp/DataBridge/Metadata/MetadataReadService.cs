using System.Text;
using static DataBridge.ApplicationDataReaderExtensions;
using NodaTime;
using System.Data.Common;
using DataBridge.Persistence;

using Shared.Messaging;

namespace DataBridge.Metadata;

public sealed class MetadataReadService(ApplicationDatabase dataSource) : IMetadataReadService
{
    public async Task<MetadataDetailDto?> GetDetailAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.GetDetailAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var series = IsDbNull(reader, "series_name")
            ? null
            : new SeriesDto
            {
                SeriesName = GetString(reader, "series_name"),
                SeasonCount = GetNullableInt32(reader, "season_count"),
                SeasonNumber = GetInt32(reader, "season_number"),
                SeasonName = GetNullableString(reader, "season_name"),
                EpisodeNumber = GetInt32(reader, "episode_number"),
                EpisodeName = GetString(reader, "episode_name")
            };

        var music = IsDbNull(reader, "album_title")
            ? null
            : new MusicDto
            {
                AlbumTitle = GetString(reader, "album_title"),
                AlbumType = GetNullableString(reader, "album_type"),
                DiscNumber = GetNullableInt32(reader, "disc_number"),
                ReleaseYear = GetNullableInt32(reader, "release_year"),
                TrackTitle = GetString(reader, "track_title"),
                TrackNumber = GetInt32(reader, "track_number"),
                Composer = GetNullableString(reader, "composer")
            };

        return new MetadataDetailDto
        {
            MediaGuid = GetGuid(reader, "media_guid"),
            Title = GetString(reader, "title"),
            Description = GetNullableString(reader, "description"),
            ThumbnailStoragePath = GetNullableString(reader, "thumbnail_storage_path"),
            DurationSeconds = GetNullableDouble(reader, "duration"),
            ReleaseDate = GetNullableInstant(reader, "release_date"),
            ViewCount = GetNullableInt64(reader, "view_count"),
            LikeCount = GetNullableInt64(reader, "like_count"),
            DislikeCount = GetNullableInt64(reader, "dislike_count"),
            AverageRating = GetNullableDouble(reader, "average_rating"),
            CommentCount = GetNullableInt64(reader, "comment_count"),
            AgeLimit = GetNullableInt32(reader, "age_limit"),
            WasLive = GetBoolean(reader, "was_live"),
            HasLiveChat = GetBoolean(reader, "has_live_chat"),
            Availability = GetNullableString(reader, "availability"),
            Location = GetNullableString(reader, "location"),
            WebpageUrl = GetNullableString(reader, "webpage_url"),
            ExternalMediaId = GetNullableString(reader, "external_media_id"),
            MetadataScrapedAt = GetInstant(reader, "metadata_scrape_date"),
            Account = new AccountDto
            {
                AccountId = GetInt64(reader, "account_id"),
                Platform = GetString(reader, "platform"),
                AccountName = GetString(reader, "account_name"),
                AccountHandle = GetString(reader, "account_handle"),
                AccountUrl = GetNullableString(reader, "account_url"),
                AccountCreationDate = GetNullableInstant(reader, "account_creation_date"),
                FollowerCount = GetNullableInt64(reader, "account_follower_count"),
                IsVerified = GetBoolean(reader, "is_verified"),
                Description = GetNullableString(reader, "account_description"),
                AvatarStoragePath = GetNullableString(reader, "avatar_storage_path"),
                BannerStoragePath = GetNullableString(reader, "banner_storage_path"),
                MediaCount = GetInt64(reader, "account_media_count")
            },
            Tags = GetJsonList<string>(reader, "tags_json"),
            Categories = GetJsonList<string>(reader, "categories_json"),
            Genres = GetJsonList<string>(reader, "genres_json"),
            Cast = GetJsonList<string>(reader, "cast_json"),
            Artists = GetJsonList<string>(reader, "artists_json"),
            AlbumArtists = GetJsonList<string>(reader, "album_artists_json"),
            Series = series,
            Music = music,
            CaptionLanguages = GetJsonList<CaptionLanguageDto>(reader, "caption_languages_json")
        };
    }

    public async Task<Guid?> GetRandomMediaGuidAsync(Guid? excludeMediaGuid, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.GetRandomMediaGuidAsync.1"));
        command.Parameters.Add("@exclude_media_guid", ApplicationParameterType.Guid).Value = (object?)excludeMediaGuid ?? DBNull.Value;

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : result is Guid mediaGuid ? mediaGuid : Guid.Parse(Convert.ToString(result)!);
    }

    public async Task<MetadataTechnicalDto?> GetTechnicalAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var baseCommand = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.GetTechnicalAsync.1"));
        baseCommand.Parameters.AddWithValue("@media_guid", mediaGuid);

        Guid actualMediaGuid;
        long durationTicks;
        TechnicalFormatDto? format;

        await using (var reader = await baseCommand.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return null;

            actualMediaGuid = GetGuid(reader, "media_guid");
            durationTicks = GetInt64(reader, "duration_ticks");
            format = IsDbNull(reader, "format_long_names")
                ? null
                : new TechnicalFormatDto
                {
                    DurationTicks = GetInt64(reader, "format_duration_ticks"),
                    StartTimeTicks = GetInt64(reader, "start_time_ticks"),
                    FormatLongNames = GetString(reader, "format_long_names"),
                    StreamCount = GetInt32(reader, "stream_count"),
                    BitRate = GetDouble(reader, "format_bit_rate")
                };
        }

        var streams = new List<TechnicalStreamDto>();
        await using (var streamCommand = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.GetTechnicalAsync.2")))
        {
            streamCommand.Parameters.AddWithValue("@media_guid", mediaGuid);
            await using var reader = await streamCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                streams.Add(new TechnicalStreamDto
                {
                    StreamType = GetString(reader, "stream_type"),
                    IsPrimary = GetBoolean(reader, "is_primary"),
                    CodecName = GetString(reader, "codec_name"),
                    CodecLongName = GetString(reader, "codec_long_name"),
                    BitRate = GetInt64(reader, "bit_rate"),
                    BitDepth = GetNullableInt32(reader, "bit_depth"),
                    DurationTicks = GetInt64(reader, "duration_ticks"),
                    Language = GetNullableString(reader, "language"),
                    Video = IsDbNull(reader, "width")
                        ? null
                        : new VideoStreamDetailDto
                        {
                            Width = GetInt32(reader, "width"),
                            Height = GetInt32(reader, "height"),
                            AvgFrameRate = GetDouble(reader, "avg_frame_rate"),
                            HdrType = GetString(reader, "hdr_type"),
                            ColorSpace = GetString(reader, "color_space"),
                            Profile = GetString(reader, "video_profile")
                        },
                    Audio = IsDbNull(reader, "channels")
                        ? null
                        : new AudioStreamDetailDto
                        {
                            Channels = GetInt32(reader, "channels"),
                            ChannelLayout = GetString(reader, "channel_layout"),
                            SampleRateHz = GetInt32(reader, "sample_rate_hz"),
                            Profile = GetString(reader, "audio_profile")
                        }
                });
            }
        }

        var chapters = new List<TechnicalChapterDto>();
        await using (var chapterCommand = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.GetTechnicalAsync.3")))
        {
            chapterCommand.Parameters.AddWithValue("@media_guid", mediaGuid);
            await using var reader = await chapterCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                chapters.Add(new TechnicalChapterDto
                {
                    Title = GetString(reader, "title"),
                    StartTicks = GetInt64(reader, "start_ticks"),
                    EndTicks = GetInt64(reader, "end_ticks")
                });
            }
        }

        return new MetadataTechnicalDto
        {
            MediaGuid = actualMediaGuid,
            DurationTicks = durationTicks,
            Format = format,
            Streams = streams,
            Chapters = chapters
        };
    }

    public async Task<IReadOnlyList<MetadataVersionDto>> ListVersionsAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.ListVersionsAsync.1"));
        command.Parameters.AddWithValue("@media_guid", mediaGuid);

        var items = new List<MetadataVersionDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MetadataVersionDto
            {
                MediaGuid = GetGuid(reader, "media_guid"),
                VersionNum = GetInt32(reader, "version_num"),
                StorageKey = GetString(reader, "storage_key"),
                StoragePath = GetString(reader, "storage_path"),
                ContentHashXxh128 = GetString(reader, "content_hash_xxh128"),
                IngestOrigin = GetString(reader, "ingest_origin")
            });
        }

        return items;
    }

    public async Task<AccountsListResult> ListAccountsAsync(int pageSize, string? after, string? platform, CancellationToken ct = default)
    {
        var cursor = DecodeCursor(after);
        var fetchSize = Math.Clamp(pageSize, 1, 100) + 1;

        await using var command = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.ListAccountsAsync.1"));
        command.Parameters.Add("@platform", ApplicationParameterType.Text).Value = (object?)Normalize(platform) ?? DBNull.Value;
        command.Parameters.Add("@after_handle", ApplicationParameterType.Text).Value = (object?)cursor?.Handle ?? DBNull.Value;
        command.Parameters.Add("@after_id", ApplicationParameterType.Int64).Value = (object?)cursor?.Id ?? DBNull.Value;
        command.Parameters.Add("@page_size", ApplicationParameterType.Integer).Value = fetchSize;

        var items = new List<AccountSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new AccountSummaryDto
            {
                AccountId = GetInt64(reader, "id"),
                Platform = GetString(reader, "platform"),
                AccountName = GetString(reader, "account_name"),
                AccountHandle = GetString(reader, "account_handle"),
                AccountUrl = GetNullableString(reader, "account_url"),
                FollowerCount = GetNullableInt64(reader, "account_follower_count"),
                IsVerified = GetBoolean(reader, "is_verified"),
                AvatarStoragePath = GetNullableString(reader, "avatar_storage_path"),
                MediaCount = GetInt64(reader, "media_count")
            });
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        var nextCursor = hasMore && items.Count > 0
            ? EncodeCursor(items[^1].AccountHandle, items[^1].AccountId)
            : null;

        return new AccountsListResult(items, nextCursor, hasMore);
    }

    public async Task<AccountDto?> GetAccountAsync(long accountId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.GetAccountAsync.1"));
        command.Parameters.AddWithValue("@account_id", accountId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new AccountDto
        {
            AccountId = GetInt64(reader, "id"),
            Platform = GetString(reader, "platform"),
            AccountName = GetString(reader, "account_name"),
            AccountHandle = GetString(reader, "account_handle"),
            AccountUrl = GetNullableString(reader, "account_url"),
            AccountCreationDate = GetNullableInstant(reader, "account_creation_date"),
            FollowerCount = GetNullableInt64(reader, "account_follower_count"),
            IsVerified = GetBoolean(reader, "is_verified"),
            Description = GetNullableString(reader, "account_description"),
            AvatarStoragePath = GetNullableString(reader, "avatar_storage_path"),
            BannerStoragePath = GetNullableString(reader, "banner_storage_path"),
            MediaCount = GetInt64(reader, "media_count")
        };
    }

    public async Task<TaxonomyListResult> ListTaxonomyAsync(
        MetadataTaxonomyKind kind,
        int pageSize,
        int pageOffset,
        string? search,
        CancellationToken ct = default)
    {
        var (table, nameColumn, junctionTable, refColumn) = kind switch
        {
            MetadataTaxonomyKind.Tags => ("metadata.tags", "tag_name", "metadata.media_tags", "tag_id"),
            MetadataTaxonomyKind.Categories => ("metadata.categories", "category_name", "metadata.media_categories", "category_id"),
            MetadataTaxonomyKind.Genres => ("metadata.genres", "genre_name", "metadata.media_genres", "genre_id"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        var limit = Math.Clamp(pageSize, 1, 100);
        var offset = Math.Max(pageOffset, 0);
        var searchValue = Normalize(search);

        await using var countCommand = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.ListTaxonomyAsync.1", dataSource.Table(table), nameColumn));
        countCommand.Parameters.Add("@search", ApplicationParameterType.Text).Value = (object?)searchValue ?? DBNull.Value;
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct));

        await using var command = dataSource.CreateCommand(dataSource.Sql("MetadataReadService.ListTaxonomyAsync.2", nameColumn, dataSource.Table(table), dataSource.Table(junctionTable), refColumn, nameColumn, nameColumn, nameColumn));
        command.Parameters.Add("@search", ApplicationParameterType.Text).Value = (object?)searchValue ?? DBNull.Value;
        command.Parameters.Add("@page_size", ApplicationParameterType.Integer).Value = limit;
        command.Parameters.Add("@page_offset", ApplicationParameterType.Integer).Value = offset;

        var items = new List<TaxonomyItemDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new TaxonomyItemDto
            {
                Name = GetString(reader, "name"),
                MediaCount = GetInt64(reader, "media_count")
            });
        }

        return new TaxonomyListResult(items, total);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EncodeCursor(string handle, long id)
    {
        var raw = Encoding.UTF8.GetBytes(handle + "\n" + id.ToStringInvariant());
        return Convert.ToBase64String(raw)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static AccountCursor? DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var padded = value.Trim()
                .Replace('-', '+')
                .Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var separator = decoded.LastIndexOf('\n');
            if (separator <= 0 || separator == decoded.Length - 1)
                throw new FormatException("Missing cursor separator.");

            var handle = decoded[..separator];
            if (!long.TryParse(decoded[(separator + 1)..], out var id))
                throw new FormatException("Invalid account id.");

            return new AccountCursor(handle, id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidMetadataCursorException("The account cursor is invalid.");
        }
    }

    private sealed record AccountCursor(string Handle, long Id);
}

file static class LongFormattingExtensions
{
    public static string ToStringInvariant(this long value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
