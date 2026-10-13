using static DataBridge.ApplicationDataReaderExtensions;
using NodaTime;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;

namespace DataBridge.Statistics;

public sealed class StatisticsReadService(ApplicationDatabase dataSource) : IStatisticsReadService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private string ClassifiedMediaCte => dataSource.Sql("StatisticsReadService.Fields.1");

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
        string? search,
        CancellationToken ct = default)
    {
        pageSize = NormalizePageSize(pageSize);
        page = Math.Max(1, page);
        var offset = (page - 1) * pageSize;
        var orderBy = ChannelOrderBy(sortBy, sortOrder);
        var normalizedSearch = search?.Trim() ?? string.Empty;
        var searchPattern = $"%{EscapeLikePattern(normalizedSearch)}%";
        var searchWhere = dataSource.Sql("StatisticsReadService.ListChannelsAsync.1");

        var totalCount = await GetChannelWithMediaCountAsync(normalizedSearch, searchPattern, ct);
        var sql = AccountSummarySql(dataSource.Sql("StatisticsReadService.ListChannelsAsync.2", searchWhere, orderBy));

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("@search", normalizedSearch);
        command.Parameters.AddWithValue("@search_pattern", searchPattern);
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

    public async Task<IReadOnlyList<ChannelSuggestionDto>> SuggestChannelsAsync(
        string? search,
        int limit,
        CancellationToken ct = default)
    {
        var normalizedSearch = search?.Trim() ?? string.Empty;
        if (normalizedSearch.Length < 2)
            return [];

        limit = Math.Clamp(limit <= 0 ? 8 : limit, 1, 20);
        var containsPattern = $"%{EscapeLikePattern(normalizedSearch)}%";
        var prefixPattern = $"{EscapeLikePattern(normalizedSearch)}%";
        var sql = AccountSummarySql(dataSource.Sql("StatisticsReadService.SuggestChannelsAsync.1"));

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("@contains_pattern", containsPattern);
        command.Parameters.AddWithValue("@prefix_pattern", prefixPattern);
        command.Parameters.AddWithValue("@limit", limit);

        var items = new List<ChannelSuggestionDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var accountName = GetNullableString(reader, "account_name");
            var accountHandle = GetNullableString(reader, "account_handle");
            var platform = GetString(reader, "platform");
            var value = FirstNonBlank(accountName, accountHandle, platform) ?? platform;
            items.Add(new ChannelSuggestionDto
            {
                Value = value,
                Label = value,
                CreatorSourceId = GetNullableInt64(reader, "creator_source_id"),
                AccountId = GetNullableInt64(reader, "account_id"),
                AccountName = accountName,
                AccountHandle = accountHandle,
                Platform = platform,
                AvailableCount = GetInt64(reader, "available_count")
            });
        }

        return items;
    }

    public async Task<ChannelStatisticsDetailDto?> GetChannelAsync(long creatorSourceId, CancellationToken ct = default)
    {
        var summarySql = ChannelSummarySql(dataSource.Sql("StatisticsReadService.GetChannelAsync.1"));
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

    // Channel search and the channel table are keyed on accounts, and most accounts have no creator
    // source behind them (ad-hoc downloads are never discovered through one), so the detail view has
    // to be resolvable by account too. Discovery-derived counts stay zero when nothing links back.
    public async Task<ChannelStatisticsDetailDto?> GetChannelByAccountAsync(long accountId, CancellationToken ct = default)
    {
        var summarySql = AccountSummarySql(dataSource.Sql("StatisticsReadService.GetChannelByAccountAsync.1"));
        ChannelStatisticsSummaryDto? summary = null;
        await using (var command = dataSource.CreateCommand(summarySql))
        {
            command.Parameters.AddWithValue("@account_id", accountId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                summary = ReadChannelSummary(reader);
            }
        }

        if (summary is null)
            return null;

        var statusCounts = await GetAccountStatusCountsAsync(accountId, ct);
        var mediaTypes = await GetAccountMediaTypesAsync(accountId, ct);
        var downloadStates = await GetAccountDownloadStatesAsync(accountId, ct);

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
        // download_daily_activity is keyed on a UTC `date`, so the buckets have to be built in whole
        // UTC days too. Bucketing on the raw timestamps instead truncated the trailing partial bucket
        // down to `@to::date`, which is exclusive — so activity recorded on the request's own end day
        // (i.e. everything downloaded today) fell outside every bucket and the charts read empty.
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetDownloadHistoryAsync.1"));
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

    // Replaces the old admin-stats pattern of paging through every channel and fetching each one's
    // detail just to sum four numbers — that grew linearly with channel count. discovery_status lives
    // once per discovered item regardless of how many channels/accounts it's viewed through, so a
    // couple of grouped aggregates give the same totals in two queries whose row count never grows
    // past the (small, fixed) number of statuses/platforms.
    private const string ExcludedDiscoveryStatuses = "('Ignored', 'PossiblyUnavailable', 'Unavailable', 'RemovedFromSource')";

    public async Task<CoverageSummaryDto> GetCoverageSummaryAsync(CancellationToken ct = default)
    {
        long available = 0, unavailable = 0, ignored = 0, removed = 0;
        await using (var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetCoverageSummaryAsync.1", ExcludedDiscoveryStatuses)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                available = GetInt64(reader, "available_count");
                unavailable = GetInt64(reader, "unavailable_count");
                ignored = GetInt64(reader, "ignored_count");
                removed = GetInt64(reader, "removed_count");
            }
        }

        var platforms = new List<PlatformCoverageDto>();
        await using (var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetCoverageSummaryAsync.2", ExcludedDiscoveryStatuses)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                platforms.Add(new PlatformCoverageDto
                {
                    Platform = GetString(reader, "platform"),
                    AvailableCount = GetInt64(reader, "available_count")
                });
            }
        }

        return new CoverageSummaryDto
        {
            AvailableCount = available,
            UnavailableCount = unavailable,
            IgnoredCount = ignored,
            RemovedCount = removed,
            Platforms = platforms
        };
    }

    private async Task<InventoryStatisticsDto> GetInventoryAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetInventoryAsync.1", ClassifiedMediaCte));

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
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetMediaTypesAsync.1", ClassifiedMediaCte));

        return await ReadMediaTypesAsync(command, ct);
    }

    private async Task<IReadOnlyList<DownloadStateStatisticsDto>> GetDownloadStatesAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetDownloadStatesAsync.1"));

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

        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetWatchStatisticsAsync.1"));
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

    private async Task<int> GetChannelWithMediaCountAsync(
        string search,
        string searchPattern,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetChannelWithMediaCountAsync.1"));
        command.Parameters.AddWithValue("@search", search);
        command.Parameters.AddWithValue("@search_pattern", searchPattern);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }

    // Channel statistics are keyed on the accounts that media actually belongs to, so ad-hoc
    // (non-subscribed) downloads still surface. Scan timestamps and source type are pulled from a
    // matching creator source when one exists, otherwise left null.
    private string AccountSummarySql(string suffix) => dataSource.Sql("StatisticsReadService.AccountSummarySql.1", ClassifiedMediaCte, suffix);

    private string ChannelSummarySql(string suffix) => dataSource.Sql("StatisticsReadService.ChannelSummarySql.1", ClassifiedMediaCte, suffix);

    private static string ChannelOrderBy(string sortBy, string sortOrder)
    {
        var field = sortBy.Trim().ToLowerInvariant() switch
        {
            "available" => "account_rollup.available_count",
            "duration" => "account_rollup.downloaded_duration_seconds",
            "bytes" => "account_rollup.total_bytes",
            "name" => "COALESCE(account_rollup.account_name, account_rollup.account_handle)",
            _ => "account_rollup.downloaded_count"
        };
        var direction = string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        return $"{field} {direction}, account_rollup.account_id ASC";
    }

    private async Task<IReadOnlyDictionary<string, long>> GetChannelStatusCountsAsync(long creatorSourceId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetChannelStatusCountsAsync.1"));
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
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetChannelMediaTypesAsync.1", ClassifiedMediaCte));
        command.Parameters.AddWithValue("@creator_source_id", creatorSourceId);

        return await ReadMediaTypesAsync(command, ct);
    }

    // jobs.download_jobs is not durable (DownloadHistoryPurger deletes terminal rows after ~30 days,
    // nightly), so this reads from statistics.creator_source_daily_states instead — a durable ledger
    // recorded at the moment each job reaches a state, the same pattern download_daily_activity uses
    // for the global view. See DownloadStatisticsRecorder.RecordChannelDailyStatesAsync.
    private async Task<IReadOnlyList<DownloadStateStatisticsDto>> GetChannelDownloadStatesAsync(long creatorSourceId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetChannelDownloadStatesAsync.1"));
        command.Parameters.AddWithValue("@creator_source_id", creatorSourceId);

        return await ReadDownloadStatesAsync(command, ct);
    }

    private async Task<IReadOnlyDictionary<string, long>> GetAccountStatusCountsAsync(long accountId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetAccountStatusCountsAsync.1"));
        command.Parameters.AddWithValue("@account_id", accountId);

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values[GetString(reader, "discovery_status")] = GetInt64(reader, "count");
        }

        return values;
    }

    private async Task<IReadOnlyList<MediaTypeStatisticsDto>> GetAccountMediaTypesAsync(long accountId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetAccountMediaTypesAsync.1", ClassifiedMediaCte));
        command.Parameters.AddWithValue("@account_id", accountId);

        return await ReadMediaTypesAsync(command, ct);
    }

    // Same durability concern as GetChannelDownloadStatesAsync above, keyed by account instead — reads
    // statistics.account_daily_states rather than joining the ephemeral jobs.download_jobs table.
    private async Task<IReadOnlyList<DownloadStateStatisticsDto>> GetAccountDownloadStatesAsync(long accountId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(dataSource.Sql("StatisticsReadService.GetAccountDownloadStatesAsync.1"));
        command.Parameters.AddWithValue("@account_id", accountId);

        return await ReadDownloadStatesAsync(command, ct);
    }

    private static async Task<IReadOnlyList<MediaTypeStatisticsDto>> ReadMediaTypesAsync(DbCommand command, CancellationToken ct)
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

    private static async Task<IReadOnlyList<DownloadStateStatisticsDto>> ReadDownloadStatesAsync(DbCommand command, CancellationToken ct)
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

    private static ChannelStatisticsSummaryDto ReadChannelSummary(DbDataReader reader)
    {
        var availableCount = GetInt64(reader, "available_count");
        var downloadedCount = GetInt64(reader, "downloaded_count");
        return new ChannelStatisticsSummaryDto
        {
            CreatorSourceId = GetNullableInt64(reader, "creator_source_id"),
            Platform = GetString(reader, "platform"),
            SourceType = GetNullableString(reader, "source_type"),
            SourceUrl = GetNullableString(reader, "source_url"),
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

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string EscapeLikePattern(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
