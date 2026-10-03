using NodaTime;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;

namespace DataBridge.Renditions;

/// <summary>
/// Read-only cross-cutting view over <c>media.stream_renditions</c> and <c>media.audio_renditions</c>
/// for the system-wide Jobs &gt; Encoding admin surface. Raw SQL (matching the style already used for
/// the channel-audio source query) since it spans two tables with a UNION ALL — EF Core has no
/// entity that represents "either rendition kind" to project through.
/// </summary>
public sealed class RenditionQueueRepository(ApplicationDatabase dataSource) : IRenditionQueueRepository
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

        await using var command = dataSource.CreateCommand(dataSource.Sql("RenditionQueueRepository.QueryAsync.1"));
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
