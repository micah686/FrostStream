using System.Text;
using System.Text.Json;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using Shared.Messaging;

namespace DataBridge.Metadata;

public sealed class MetadataReadService(NpgsqlDataSource dataSource) : IMetadataReadService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MetadataDetailDto?> GetDetailAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                mm.media_guid,
                COALESCE(mm.title, '') AS title,
                mm.description,
                mm.thumbnail_storage_path,
                mm.duration,
                mm.release_date,
                mm.view_count,
                mm.like_count,
                mm.dislike_count,
                mm.average_rating,
                mm.comment_count,
                mm.age_limit,
                mm.was_live,
                mm.availability::text AS availability,
                mm.location,
                mm.webpage_url,
                mm.external_media_id,
                mm.metadata_scrape_date,
                a.id AS account_id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_creation_date,
                a.account_follower_count,
                a.is_verified,
                a.account_description,
                a.avatar_storage_path,
                a.banner_storage_path,
                (SELECT COUNT(*) FROM metadata.media_metadata amm WHERE amm.account_id = a.id) AS account_media_count,
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
                    (SELECT json_agg(cm.cast_name ORDER BY cm.cast_name)
                     FROM metadata.media_cast mc
                     JOIN metadata.cast_members cm ON cm.id = mc.cast_member_id
                     WHERE mc.media_metadata_id = mm.id),
                    '[]'::json)::text AS cast_json,
                COALESCE(
                    (SELECT json_agg(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata.media_artists ma
                     JOIN metadata.artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id AND ma.is_album_artist = false),
                    '[]'::json)::text AS artists_json,
                COALESCE(
                    (SELECT json_agg(ar.artist_name ORDER BY ar.artist_name)
                     FROM metadata.media_artists ma
                     JOIN metadata.artists ar ON ar.id = ma.artist_id
                     WHERE ma.media_metadata_id = mm.id AND ma.is_album_artist = true),
                    '[]'::json)::text AS album_artists_json,
                COALESCE(
                    (SELECT json_agg(row_to_json(caption_rows))
                     FROM (
                         SELECT DISTINCT
                             c.two_digit_language_code AS "languageCode",
                             c.caption_type::text AS "captionType",
                             c.name AS "name"
                         FROM metadata.media_captions c
                         WHERE c.media_guid = mm.media_guid
                         ORDER BY c.two_digit_language_code, c.caption_type::text
                     ) caption_rows),
                    '[]'::json)::text AS caption_languages_json,
                s.series_name,
                s.season_count,
                s.season_number,
                s.season_name,
                s.episode_number,
                s.episode_name,
                m.album_title,
                m.album_type,
                m.disc_number,
                m.release_year,
                m.track_title,
                m.track_number,
                m.composer
            FROM metadata.media_metadata mm
            JOIN metadata.accounts a ON a.id = mm.account_id
            LEFT JOIN metadata.series_metadata s ON s.media_guid = mm.media_guid
            LEFT JOIN metadata.music_metadata m ON m.media_guid = mm.media_guid
            WHERE mm.media_guid = @media_guid
            """);
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

    public async Task<MetadataTechnicalDto?> GetTechnicalAsync(Guid mediaGuid, CancellationToken ct = default)
    {
        await using var baseCommand = dataSource.CreateCommand("""
            SELECT
                mb.media_guid,
                mb.duration_ticks,
                mfd.duration_ticks AS format_duration_ticks,
                mfd.start_time_ticks,
                mfd.format_long_names,
                mfd.stream_count,
                mfd.bit_rate AS format_bit_rate
            FROM metadata.media_base mb
            LEFT JOIN metadata.media_format_details mfd ON mfd.media_base_id = mb.id
            WHERE mb.media_guid = @media_guid
            """);
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
        await using (var streamCommand = dataSource.CreateCommand("""
            SELECT
                ms.stream_type,
                ms.is_primary,
                ms.codec_name,
                ms.codec_long_name,
                ms.bit_rate,
                ms.bit_depth,
                ms.duration_ticks,
                ms.language,
                vs.width,
                vs.height,
                vs.avg_frame_rate,
                vs.hdr_type,
                vs.color_space,
                vs.profile AS video_profile,
                ads.channels,
                ads.channel_layout,
                ads.sample_rate_hz,
                ads.profile AS audio_profile
            FROM metadata.media_base mb
            JOIN metadata.media_streams ms ON ms.media_base_id = mb.id
            LEFT JOIN metadata.video_stream_details vs ON vs.media_stream_id = ms.id
            LEFT JOIN metadata.audio_stream_details ads ON ads.media_stream_id = ms.id
            WHERE mb.media_guid = @media_guid
            ORDER BY ms.id
            """))
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
        await using (var chapterCommand = dataSource.CreateCommand("""
            SELECT cd.title, cd.start_ticks, cd.end_ticks
            FROM metadata.media_base mb
            JOIN metadata.chapter_data cd ON cd.media_base_id = mb.id
            WHERE mb.media_guid = @media_guid
            ORDER BY cd.start_ticks, cd.id
            """))
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

    public async Task<AccountsListResult> ListAccountsAsync(int pageSize, string? after, string? platform, CancellationToken ct = default)
    {
        var cursor = DecodeCursor(after);
        var fetchSize = Math.Clamp(pageSize, 1, 100) + 1;

        await using var command = dataSource.CreateCommand("""
            SELECT
                a.id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_follower_count,
                a.is_verified,
                a.avatar_storage_path,
                COUNT(mm.id) AS media_count
            FROM metadata.accounts a
            LEFT JOIN metadata.media_metadata mm ON mm.account_id = a.id
            WHERE (@platform IS NULL OR a.platform = @platform)
              AND (
                  @after_handle IS NULL
                  OR (a.account_handle, a.id) > (@after_handle, @after_id)
              )
            GROUP BY a.id
            ORDER BY a.account_handle, a.id
            LIMIT @page_size
            """);
        command.Parameters.Add("@platform", NpgsqlDbType.Text).Value = (object?)Normalize(platform) ?? DBNull.Value;
        command.Parameters.Add("@after_handle", NpgsqlDbType.Text).Value = (object?)cursor?.Handle ?? DBNull.Value;
        command.Parameters.Add("@after_id", NpgsqlDbType.Bigint).Value = (object?)cursor?.Id ?? DBNull.Value;
        command.Parameters.Add("@page_size", NpgsqlDbType.Integer).Value = fetchSize;

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
        await using var command = dataSource.CreateCommand("""
            SELECT
                a.id,
                a.platform,
                a.account_name,
                a.account_handle,
                a.account_url,
                a.account_creation_date,
                a.account_follower_count,
                a.is_verified,
                a.account_description,
                a.avatar_storage_path,
                a.banner_storage_path,
                COUNT(mm.id) AS media_count
            FROM metadata.accounts a
            LEFT JOIN metadata.media_metadata mm ON mm.account_id = a.id
            WHERE a.id = @account_id
            GROUP BY a.id
            """);
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

        await using var countCommand = dataSource.CreateCommand($"""
            SELECT COUNT(*)
            FROM {table} t
            WHERE (@search IS NULL OR t.{nameColumn} ILIKE @search || '%')
            """);
        countCommand.Parameters.Add("@search", NpgsqlDbType.Text).Value = (object?)searchValue ?? DBNull.Value;
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct));

        await using var command = dataSource.CreateCommand($"""
            SELECT
                t.{nameColumn} AS name,
                COUNT(j.media_metadata_id) AS media_count
            FROM {table} t
            LEFT JOIN {junctionTable} j ON j.{refColumn} = t.id
            WHERE (@search IS NULL OR t.{nameColumn} ILIKE @search || '%')
            GROUP BY t.id, t.{nameColumn}
            ORDER BY t.{nameColumn}
            LIMIT @page_size OFFSET @page_offset
            """);
        command.Parameters.Add("@search", NpgsqlDbType.Text).Value = (object?)searchValue ?? DBNull.Value;
        command.Parameters.Add("@page_size", NpgsqlDbType.Integer).Value = limit;
        command.Parameters.Add("@page_offset", NpgsqlDbType.Integer).Value = offset;

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

    private static IReadOnlyList<T> GetJsonList<T>(NpgsqlDataReader reader, string name)
    {
        var value = GetNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<T>>(value, JsonOptions) ?? [];
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

    private static bool IsDbNull(NpgsqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name));

    private static Guid GetGuid(NpgsqlDataReader reader, string name)
        => reader.GetGuid(reader.GetOrdinal(name));

    private static string GetString(NpgsqlDataReader reader, string name)
        => reader.GetString(reader.GetOrdinal(name));

    private static string? GetNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool GetBoolean(NpgsqlDataReader reader, string name)
        => reader.GetBoolean(reader.GetOrdinal(name));

    private static int GetInt32(NpgsqlDataReader reader, string name)
        => reader.GetInt32(reader.GetOrdinal(name));

    private static int? GetNullableInt32(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long GetInt64(NpgsqlDataReader reader, string name)
        => reader.GetInt64(reader.GetOrdinal(name));

    private static long? GetNullableInt64(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static double GetDouble(NpgsqlDataReader reader, string name)
        => reader.GetDouble(reader.GetOrdinal(name));

    private static double? GetNullableDouble(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static Instant GetInstant(NpgsqlDataReader reader, string name)
        => ToInstant(reader.GetDateTime(reader.GetOrdinal(name)));

    private static Instant? GetNullableInstant(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ToInstant(reader.GetDateTime(ordinal));
    }

    private static Instant ToInstant(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return Instant.FromDateTimeUtc(utc);
    }
}

file static class LongFormattingExtensions
{
    public static string ToStringInvariant(this long value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
