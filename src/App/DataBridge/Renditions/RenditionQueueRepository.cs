using NodaTime;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Renditions;

/// <summary>
/// Read-only cross-cutting view over <c>media.stream_renditions</c> and <c>media.audio_renditions</c>
/// for the system-wide Jobs &gt; Encoding admin surface. Raw SQL (matching the style already used for
/// the channel-audio source query) since it spans two tables with a UNION ALL — EF Core has no
/// entity that represents "either rendition kind" to project through.
/// </summary>
public sealed class RenditionQueueRepository(NpgsqlDataSource dataSource) : IRenditionQueueRepository
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public async Task<RenditionQueuePage> QueryAsync(RenditionQueueListRequest request, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit <= 0 ? DefaultLimit : request.Limit, 1, MaxLimit);
        var offset = DecodeCursor(request.Cursor);

        var kind = request.Kind?.ToString();
        var status = NormalizeOptional(request.Status)?.ToLowerInvariant();
        var storageKey = NormalizeOptional(request.StorageKey);
        var search = NormalizeOptional(request.Query);

        await using var command = dataSource.CreateCommand("""
            WITH combined AS (
                SELECT
                    'Stream'::text AS kind,
                    sr.rendition_id,
                    sr.media_guid,
                    sr.source_version_num,
                    sr.status::text AS status,
                    sr.storage_key,
                    sr.storage_path,
                    sr.size_bytes,
                    sr.duration_seconds,
                    sr.error_message,
                    EXTRACT(EPOCH FROM sr.created_at)::bigint AS created_at,
                    EXTRACT(EPOCH FROM sr.updated_at)::bigint AS updated_at
                FROM media.stream_renditions sr
                UNION ALL
                SELECT
                    'Audio'::text AS kind,
                    ar.rendition_id,
                    ar.media_guid,
                    ar.source_version_num,
                    ar.status::text AS status,
                    ar.storage_key,
                    ar.storage_path,
                    ar.size_bytes,
                    ar.duration_seconds,
                    ar.error_message,
                    EXTRACT(EPOCH FROM ar.created_at)::bigint AS created_at,
                    EXTRACT(EPOCH FROM ar.updated_at)::bigint AS updated_at
                FROM media.audio_renditions ar
            )
            SELECT
                c.kind,
                c.rendition_id,
                c.media_guid,
                COALESCE(NULLIF(mm.title, ''), 'Untitled') AS title,
                c.source_version_num,
                c.status,
                c.storage_key,
                c.storage_path,
                c.size_bytes,
                c.duration_seconds,
                c.error_message,
                c.created_at,
                c.updated_at,
                COUNT(*) OVER() AS total_count
            FROM combined c
            LEFT JOIN metadata.media_metadata mm ON mm.media_guid = c.media_guid
            WHERE (@kind::text IS NULL OR c.kind = @kind::text)
              AND (@status::text IS NULL OR c.status = @status::text)
              AND (@storage_key::text IS NULL OR c.storage_key = @storage_key::text)
              AND (@query::text IS NULL
                   OR mm.title ILIKE '%' || @query::text || '%'
                   OR c.media_guid::text ILIKE '%' || @query::text || '%')
            ORDER BY c.created_at DESC, c.rendition_id DESC
            LIMIT @limit OFFSET @offset
            """);
        command.Parameters.AddWithValue("@kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@storage_key", (object?)storageKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@query", (object?)search ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);

        var items = new List<RenditionQueueItemDto>();
        var totalCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RenditionQueueItemDto
            {
                Kind = Enum.Parse<RenditionKind>(reader.GetString(0)),
                RenditionId = reader.GetGuid(1),
                MediaGuid = reader.GetGuid(2),
                Title = reader.GetString(3),
                SourceVersion = reader.GetInt32(4),
                Status = ToPascalStatus(reader.GetString(5)),
                StorageKey = reader.GetString(6),
                StoragePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                SizeBytes = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                DurationSeconds = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt = Instant.FromUnixTimeSeconds(reader.GetInt64(11)),
                UpdatedAt = Instant.FromUnixTimeSeconds(reader.GetInt64(12))
            });
            totalCount = (int)reader.GetInt64(13);
        }

        var nextOffset = offset + items.Count;
        var nextCursor = nextOffset < totalCount ? EncodeCursor(nextOffset) : null;

        return new RenditionQueuePage(items, nextCursor, totalCount);
    }

    private static string ToPascalStatus(string pgStatus) => pgStatus switch
    {
        "pending" => "Pending",
        "processing" => "Processing",
        "ready" => "Ready",
        "failed" => "Failed",
        _ => pgStatus
    };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
