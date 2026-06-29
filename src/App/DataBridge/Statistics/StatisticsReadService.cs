using static DataBridge.NpgsqlDataReaderExtensions;
using NodaTime;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Statistics;

public sealed class StatisticsReadService(NpgsqlDataSource dataSource) : IStatisticsReadService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private const string ClassifiedMediaCte = """
        WITH media_bytes AS (
            SELECT msv.media_guid, MAX(dj.file_size_bytes) FILTER (WHERE dj.state = 'completed') AS size_bytes
            FROM media.media_source_versions msv
            JOIN downloads.download_jobs dj ON dj.job_id = msv.latest_job_id
            GROUP BY msv.media_guid
        ),
        stream_flags AS (
            SELECT
                mb.media_guid,
                bool_or(ms.stream_type = 'video') AS has_video,
                bool_or(ms.stream_type = 'audio') AS has_audio
            FROM metadata.media_base mb
            JOIN metadata.media_streams ms ON ms.media_base_id = mb.id
            GROUP BY mb.media_guid
        ),
        discovery_flags AS (
            SELECT
                dm.platform,
                dm.external_media_id,
                bool_or(cs.source_type = 'Shorts') AS is_shorts,
                bool_or(lower(COALESCE(dm.live_status, '')) IN ('is_live', 'was_live', 'post_live')) AS is_live
            FROM discovery.discovered_media dm
            JOIN discovery.creator_sources cs ON cs.id = dm.creator_source_id
            GROUP BY dm.platform, dm.external_media_id
        ),
        classified_media AS (
            SELECT
                mm.media_guid,
                mm.external_media_id,
                a.platform,
                mm.account_id,
                COALESCE(mm.duration, 0) AS duration_seconds,
                COALESCE(mb.size_bytes, 0) AS size_bytes,
                CASE
                    WHEN sm.media_guid IS NOT NULL THEN 'tv'
                    WHEN mm.was_live OR COALESCE(df.is_live, false) THEN 'live'
                    WHEN COALESCE(df.is_shorts, false) THEN 'shorts'
                    WHEN COALESCE(sf.has_audio, false) AND NOT COALESCE(sf.has_video, false) THEN 'audio_only'
                    WHEN COALESCE(sf.has_video, false) AND COALESCE(mm.duration, 0) >= 2400 THEN 'movies'
                    WHEN COALESCE(sf.has_video, false) THEN 'videos'
                    ELSE 'unknown'
                END AS media_type
            FROM metadata.media_metadata mm
            JOIN metadata.accounts a ON a.id = mm.account_id
            LEFT JOIN media_bytes mb ON mb.media_guid = mm.media_guid
            LEFT JOIN stream_flags sf ON sf.media_guid = mm.media_guid
            LEFT JOIN metadata.series_metadata sm ON sm.media_guid = mm.media_guid
            LEFT JOIN discovery_flags df ON df.platform = a.platform AND df.external_media_id = mm.external_media_id
        )
        """;

    public async Task<StatisticsOverviewDto> GetOverviewAsync(string? ownerSubject, CancellationToken ct = default)
    {
        var inventory = await GetInventoryAsync(ct);
        var mediaTypes = await GetMediaTypesAsync(ct);
        var downloadStates = await GetDownloadStatesAsync(ct);
        var watch = await GetWatchStatisticsAsync(ownerSubject, inventory.TotalMedia, inventory.TotalDurationSeconds, ct);

        return new StatisticsOverviewDto
        {
            Inventory = inventory,
            WatchProgress = watch,
            MediaTypes = mediaTypes,
            DownloadStates = downloadStates
        };
    }

    public async Task<(IReadOnlyList<ChannelStatisticsSummaryDto> Items, int TotalCount, int Page, bool HasMore)> ListChannelsAsync(
        int pageSize,
        int page,
        string sortBy,
        string sortOrder,
        CancellationToken ct = default)
    {
        pageSize = NormalizePageSize(pageSize);
        page = Math.Max(1, page);
        var offset = (page - 1) * pageSize;
        var orderBy = ChannelOrderBy(sortBy, sortOrder);

        var totalCount = await GetCreatorSourceCountAsync(ct);
        var sql = ChannelSummarySql($"""
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset
            """);

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("@limit", pageSize);
        command.Parameters.AddWithValue("@offset", offset);

        var items = new List<ChannelStatisticsSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(ReadChannelSummary(reader));
        }

        return (items, totalCount, page, offset + items.Count < totalCount);
    }

    public async Task<ChannelStatisticsDetailDto?> GetChannelAsync(long creatorSourceId, CancellationToken ct = default)
    {
        var summarySql = ChannelSummarySql("WHERE source_rollup.creator_source_id = @creator_source_id");
        ChannelStatisticsSummaryDto? summary = null;
        await using (var command = dataSource.CreateCommand(summarySql))
        {
            command.Parameters.AddWithValue("@creator_source_id", creatorSourceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                summary = ReadChannelSummary(reader);
            }
        }

        if (summary is null)
            return null;

        var statusCounts = await GetChannelStatusCountsAsync(creatorSourceId, ct);
        var mediaTypes = await GetChannelMediaTypesAsync(creatorSourceId, ct);
        var downloadStates = await GetChannelDownloadStatesAsync(creatorSourceId, ct);

        return new ChannelStatisticsDetailDto
        {
            Summary = summary,
            IgnoredCount = statusCounts.GetValueOrDefault("Ignored"),
            UnavailableCount = statusCounts.GetValueOrDefault("Unavailable") +
                statusCounts.GetValueOrDefault("PossiblyUnavailable"),
            RemovedCount = statusCounts.GetValueOrDefault("RemovedFromSource"),
            MediaTypes = mediaTypes,
            RecentDownloadStates = downloadStates
        };
    }

    public async Task<IReadOnlyList<DownloadHistoryBucketDto>> GetDownloadHistoryAsync(
        StatisticsDownloadHistoryRequestMessage request,
        CancellationToken ct = default)
    {
        var step = NormalizeBucketStep(request.Bucket);
        await using var command = dataSource.CreateCommand("""
            WITH buckets AS (
                SELECT
                    bucket_start,
                    LEAST(bucket_start + @step::interval, @to) AS bucket_end
                FROM generate_series(@from, @to, @step::interval) AS bucket_start
                WHERE bucket_start < @to
            )
            SELECT
                b.bucket_start,
                b.bucket_end,
                (SELECT COUNT(*) FROM downloads.download_jobs dj
                 WHERE dj.created_at >= b.bucket_start AND dj.created_at < b.bucket_end) AS created,
                (SELECT COUNT(*) FROM downloads.download_jobs dj
                 WHERE dj.completed_at >= b.bucket_start AND dj.completed_at < b.bucket_end AND dj.state = 'completed') AS completed,
                (SELECT COUNT(*) FROM downloads.download_jobs dj
                 WHERE dj.updated_at >= b.bucket_start AND dj.updated_at < b.bucket_end
                   AND dj.state IN ('failed_transient', 'failed_permanent', 'dead_lettered')) AS failed,
                (SELECT COUNT(*) FROM downloads.download_jobs dj
                 WHERE dj.updated_at >= b.bucket_start AND dj.updated_at < b.bucket_end AND dj.state = 'cancelled') AS cancelled,
                (SELECT COUNT(*) FROM downloads.download_jobs dj
                 WHERE dj.updated_at >= b.bucket_start AND dj.updated_at < b.bucket_end AND dj.state = 'ignored') AS ignored,
                COALESCE(completed_rollup.bytes_completed, 0) AS bytes_completed,
                COALESCE(completed_rollup.duration_completed_seconds, 0) AS duration_completed_seconds
            FROM buckets b
            LEFT JOIN LATERAL (
                SELECT
                    COALESCE(SUM(job_bytes), 0)::bigint AS bytes_completed,
                    COALESCE(SUM(duration_seconds), 0) AS duration_completed_seconds
                FROM (
                    SELECT
                        dj.job_id,
                        COALESCE(dj.file_size_bytes, 0) AS job_bytes,
                        MAX(COALESCE(mm.duration, 0)) AS duration_seconds
                    FROM downloads.download_jobs dj
                    LEFT JOIN media.media_source_versions msv ON msv.latest_job_id = dj.job_id
                    LEFT JOIN metadata.media_metadata mm ON mm.media_guid = msv.media_guid
                    WHERE dj.completed_at >= b.bucket_start
                      AND dj.completed_at < b.bucket_end
                      AND dj.state = 'completed'
                    GROUP BY dj.job_id, dj.file_size_bytes
                ) completed_jobs
            ) completed_rollup ON true
            ORDER BY b.bucket_start
            """);
        command.Parameters.AddWithValue("@from", request.From.ToDateTimeOffset());
        command.Parameters.AddWithValue("@to", request.To.ToDateTimeOffset());
        command.Parameters.AddWithValue("@step", step);

        var buckets = new List<DownloadHistoryBucketDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            buckets.Add(new DownloadHistoryBucketDto
            {
                BucketStart = GetInstant(reader, "bucket_start"),
                BucketEnd = GetInstant(reader, "bucket_end"),
                Created = GetInt64(reader, "created"),
                Completed = GetInt64(reader, "completed"),
                Failed = GetInt64(reader, "failed"),
                Cancelled = GetInt64(reader, "cancelled"),
                Ignored = GetInt64(reader, "ignored"),
                BytesCompleted = GetInt64(reader, "bytes_completed"),
                DurationCompletedSeconds = GetDouble(reader, "duration_completed_seconds")
            });
        }

        return buckets;
    }

    private async Task<InventoryStatisticsDto> GetInventoryAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            {ClassifiedMediaCte}
            SELECT
                (SELECT COUNT(*) FROM media.media) AS total_media,
                (SELECT COUNT(*) FROM metadata.accounts) AS total_channels,
                (SELECT COUNT(*) FROM discovery.creator_sources) AS total_creator_sources,
                (SELECT COUNT(*) FROM playlists.playlists) AS total_playlists,
                (SELECT COUNT(*) FROM downloads.download_jobs) AS total_downloads,
                COALESCE((SELECT SUM(file_size_bytes) FROM downloads.download_jobs WHERE state = 'completed'), 0)::bigint AS total_bytes,
                COALESCE((SELECT SUM(duration_seconds) FROM classified_media), 0) AS total_duration_seconds
            """);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new InventoryStatisticsDto
        {
            TotalMedia = GetInt64(reader, "total_media"),
            TotalChannels = GetInt64(reader, "total_channels"),
            TotalCreatorSources = GetInt64(reader, "total_creator_sources"),
            TotalPlaylists = GetInt64(reader, "total_playlists"),
            TotalDownloads = GetInt64(reader, "total_downloads"),
            TotalBytes = GetInt64(reader, "total_bytes"),
            TotalDurationSeconds = GetDouble(reader, "total_duration_seconds")
        };
    }

    private async Task<IReadOnlyList<MediaTypeStatisticsDto>> GetMediaTypesAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            {ClassifiedMediaCte}
            SELECT
                media_type,
                COUNT(*) AS count,
                COALESCE(SUM(duration_seconds), 0) AS duration_seconds,
                COALESCE(SUM(size_bytes), 0)::bigint AS bytes
            FROM classified_media
            GROUP BY media_type
            ORDER BY count DESC, media_type
            """);

        return await ReadMediaTypesAsync(command, ct);
    }

    private async Task<IReadOnlyList<DownloadStateStatisticsDto>> GetDownloadStatesAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT state::text AS state, COUNT(*) AS count
            FROM downloads.download_jobs
            GROUP BY state
            ORDER BY count DESC, state
            """);

        return await ReadDownloadStatesAsync(command, ct);
    }

    private async Task<WatchStatisticsDto> GetWatchStatisticsAsync(
        string? ownerSubject,
        long totalMedia,
        double totalDurationSeconds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject) || totalMedia == 0)
        {
            return new WatchStatisticsDto
            {
                UnwatchedCount = totalMedia,
                UnwatchedPercent = totalMedia == 0 ? 0 : 100
            };
        }

        await using var command = dataSource.CreateCommand("""
            SELECT
                COUNT(*) FILTER (WHERE completed) AS watched_count,
                COALESCE(SUM(
                    CASE
                        WHEN position_seconds IS NULL THEN 0
                        WHEN duration_seconds IS NULL THEN position_seconds
                        ELSE LEAST(position_seconds, duration_seconds)
                    END), 0) AS watch_progress_seconds
            FROM media.watch_states
            WHERE owner_subject = @owner_subject
            """);
        command.Parameters.AddWithValue("@owner_subject", ownerSubject);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var watched = Math.Min(GetInt64(reader, "watched_count"), totalMedia);
        var unwatched = Math.Max(0, totalMedia - watched);
        var progress = GetDouble(reader, "watch_progress_seconds");

        return new WatchStatisticsDto
        {
            WatchedCount = watched,
            WatchedPercent = Percent(watched, totalMedia),
            UnwatchedCount = unwatched,
            UnwatchedPercent = Percent(unwatched, totalMedia),
            WatchProgressSeconds = progress,
            WatchProgressPercent = Percent(progress, totalDurationSeconds)
        };
    }

    private async Task<int> GetCreatorSourceCountAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT COUNT(*) FROM discovery.creator_sources");
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }

    private static string ChannelSummarySql(string suffix) => $"""
        {ClassifiedMediaCte},
        source_downloaded_media AS (
            SELECT DISTINCT
                dm.creator_source_id,
                cm.media_guid,
                cm.duration_seconds,
                cm.size_bytes
            FROM discovery.discovered_media dm
            JOIN classified_media cm ON cm.platform = dm.platform AND cm.external_media_id = dm.external_media_id
        ),
        source_rollup AS (
            SELECT
                cs.id AS creator_source_id,
                cs.platform,
                cs.source_type,
                cs.source_url,
                cs.last_successful_scan_at,
                cs.last_full_scan_at,
                COUNT(dm.id) FILTER (
                    WHERE dm.discovery_status NOT IN ('Ignored', 'Unavailable', 'RemovedFromSource')
                ) AS available_count,
                COALESCE(downloaded_rollup.downloaded_count, 0) AS downloaded_count,
                COALESCE(SUM(dm.duration_seconds) FILTER (
                    WHERE dm.discovery_status NOT IN ('Ignored', 'Unavailable', 'RemovedFromSource')
                ), 0) AS total_duration_seconds,
                COALESCE(downloaded_rollup.downloaded_duration_seconds, 0) AS downloaded_duration_seconds,
                COALESCE(downloaded_rollup.total_bytes, 0)::bigint AS total_bytes
            FROM discovery.creator_sources cs
            LEFT JOIN discovery.discovered_media dm ON dm.creator_source_id = cs.id
            LEFT JOIN LATERAL (
                SELECT
                    COUNT(*) AS downloaded_count,
                    COALESCE(SUM(duration_seconds), 0) AS downloaded_duration_seconds,
                    COALESCE(SUM(size_bytes), 0)::bigint AS total_bytes
                FROM source_downloaded_media sdm
                WHERE sdm.creator_source_id = cs.id
            ) downloaded_rollup ON true
            GROUP BY cs.id, cs.platform, cs.source_type, cs.source_url, cs.last_successful_scan_at, cs.last_full_scan_at
                , downloaded_rollup.downloaded_count, downloaded_rollup.downloaded_duration_seconds, downloaded_rollup.total_bytes
        )
        SELECT
            source_rollup.*,
            CASE WHEN available_count = 0 THEN 0 ELSE downloaded_count::double precision * 100 / available_count END AS downloaded_percent,
            account_rollup.account_id,
            account_rollup.account_name,
            account_rollup.account_handle,
            account_rollup.avatar_storage_path
        FROM source_rollup
        LEFT JOIN LATERAL (
            SELECT
                a.id AS account_id,
                a.account_name,
                a.account_handle,
                a.avatar_storage_path,
                COUNT(*) AS linked_count
            FROM discovery.discovered_media dm
            JOIN metadata.media_metadata mm ON mm.external_media_id = dm.external_media_id
            JOIN metadata.accounts a ON a.id = mm.account_id AND a.platform = dm.platform
            WHERE dm.creator_source_id = source_rollup.creator_source_id
            GROUP BY a.id, a.account_name, a.account_handle, a.avatar_storage_path
            ORDER BY linked_count DESC, a.id
            LIMIT 1
        ) account_rollup ON true
        {suffix}
        """;

    private static string ChannelOrderBy(string sortBy, string sortOrder)
    {
        var field = sortBy.Trim().ToLowerInvariant() switch
        {
            "available" => "available_count",
            "duration" => "downloaded_duration_seconds",
            "bytes" => "total_bytes",
            "name" => "COALESCE(account_rollup.account_name, source_rollup.source_url)",
            _ => "downloaded_count"
        };
        var direction = string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        return $"{field} {direction}, source_rollup.creator_source_id ASC";
    }

    private async Task<IReadOnlyDictionary<string, long>> GetChannelStatusCountsAsync(long creatorSourceId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT discovery_status, COUNT(*) AS count
            FROM discovery.discovered_media
            WHERE creator_source_id = @creator_source_id
            GROUP BY discovery_status
            """);
        command.Parameters.AddWithValue("@creator_source_id", creatorSourceId);

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values[GetString(reader, "discovery_status")] = GetInt64(reader, "count");
        }

        return values;
    }

    private async Task<IReadOnlyList<MediaTypeStatisticsDto>> GetChannelMediaTypesAsync(long creatorSourceId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            {ClassifiedMediaCte}
            SELECT
                cm.media_type,
                COUNT(*) AS count,
                COALESCE(SUM(cm.duration_seconds), 0) AS duration_seconds,
                COALESCE(SUM(cm.size_bytes), 0)::bigint AS bytes
            FROM (
                SELECT DISTINCT
                    cm.media_guid,
                    cm.media_type,
                    cm.duration_seconds,
                    cm.size_bytes
                FROM discovery.discovered_media dm
                JOIN classified_media cm ON cm.platform = dm.platform AND cm.external_media_id = dm.external_media_id
                WHERE dm.creator_source_id = @creator_source_id
            ) cm
            GROUP BY cm.media_type
            ORDER BY count DESC, cm.media_type
            """);
        command.Parameters.AddWithValue("@creator_source_id", creatorSourceId);

        return await ReadMediaTypesAsync(command, ct);
    }

    private async Task<IReadOnlyList<DownloadStateStatisticsDto>> GetChannelDownloadStatesAsync(long creatorSourceId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT dj.state::text AS state, COUNT(*) AS count
            FROM discovery.discovered_media dm
            JOIN downloads.download_jobs dj ON dj.source_url = dm.canonical_url
            WHERE dm.creator_source_id = @creator_source_id
            GROUP BY dj.state
            ORDER BY count DESC, state
            """);
        command.Parameters.AddWithValue("@creator_source_id", creatorSourceId);

        return await ReadDownloadStatesAsync(command, ct);
    }

    private static async Task<IReadOnlyList<MediaTypeStatisticsDto>> ReadMediaTypesAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var items = new List<MediaTypeStatisticsDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MediaTypeStatisticsDto
            {
                Type = GetString(reader, "media_type"),
                Count = GetInt64(reader, "count"),
                DurationSeconds = GetDouble(reader, "duration_seconds"),
                Bytes = GetInt64(reader, "bytes")
            });
        }

        return items;
    }

    private static async Task<IReadOnlyList<DownloadStateStatisticsDto>> ReadDownloadStatesAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var items = new List<DownloadStateStatisticsDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new DownloadStateStatisticsDto
            {
                State = GetString(reader, "state"),
                Count = GetInt64(reader, "count")
            });
        }

        return items;
    }

    private static ChannelStatisticsSummaryDto ReadChannelSummary(NpgsqlDataReader reader)
    {
        var availableCount = GetInt64(reader, "available_count");
        var downloadedCount = GetInt64(reader, "downloaded_count");
        return new ChannelStatisticsSummaryDto
        {
            CreatorSourceId = GetInt64(reader, "creator_source_id"),
            Platform = GetString(reader, "platform"),
            SourceType = GetString(reader, "source_type"),
            SourceUrl = GetString(reader, "source_url"),
            AccountId = GetNullableInt64(reader, "account_id"),
            AccountName = GetNullableString(reader, "account_name"),
            AccountHandle = GetNullableString(reader, "account_handle"),
            AvatarStoragePath = GetNullableString(reader, "avatar_storage_path"),
            AvailableCount = availableCount,
            DownloadedCount = downloadedCount,
            DownloadedPercent = Percent(downloadedCount, availableCount),
            TotalDurationSeconds = GetDouble(reader, "total_duration_seconds"),
            DownloadedDurationSeconds = GetDouble(reader, "downloaded_duration_seconds"),
            TotalBytes = GetInt64(reader, "total_bytes"),
            LastSuccessfulScanAt = GetNullableInstant(reader, "last_successful_scan_at"),
            LastFullScanAt = GetNullableInstant(reader, "last_full_scan_at")
        };
    }

    private static int NormalizePageSize(int pageSize)
        => Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

    private static string NormalizeBucketStep(string bucket)
        => bucket.Trim().ToLowerInvariant() switch
        {
            "day" => "1 day",
            "week" => "1 week",
            "month" => "1 month",
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Unsupported download history bucket.")
        };

    private static double Percent(double numerator, double denominator)
        => denominator <= 0 ? 0 : numerator * 100 / denominator;
}
