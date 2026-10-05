namespace DataBridge.Persistence.Queries;

internal static class StatisticsQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["StatisticsReadService.Fields.1"] = new(
            """
            WITH media_bytes AS (
                SELECT DISTINCT ON (cmd.media_guid) cmd.media_guid, cmd.bytes AS size_bytes
                FROM statistics.channel_media_downloads cmd
                ORDER BY cmd.media_guid, cmd.completed_at DESC
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
            """,
            """
            WITH media_bytes AS (
                SELECT media_guid, size_bytes FROM (SELECT cmd.media_guid, cmd.bytes AS size_bytes, row_number() OVER (PARTITION BY cmd.media_guid ORDER BY cmd.completed_at DESC, cmd.id DESC) AS ordinal FROM statistics_channel_media_downloads cmd) WHERE ordinal = 1
            ),
            stream_flags AS (
                SELECT
                    mb.media_guid,
                    MAX(ms.stream_type = 'video') AS has_video,
                    MAX(ms.stream_type = 'audio') AS has_audio
                FROM metadata_media_base mb
                JOIN metadata_media_streams ms ON ms.media_base_id = mb.id
                GROUP BY mb.media_guid
            ),
            discovery_flags AS (
                SELECT
                    dm.platform,
                    dm.external_media_id,
                    MAX(cs.source_type = 'Shorts') AS is_shorts,
                    MAX(lower(COALESCE(dm.live_status, '')) IN ('is_live', 'was_live', 'post_live')) AS is_live
                FROM discovery_discovered_media dm
                JOIN discovery_creator_sources cs ON cs.id = dm.creator_source_id
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
                FROM metadata_media_metadata mm
                JOIN metadata_accounts a ON a.id = mm.account_id
                LEFT JOIN media_bytes mb ON mb.media_guid = mm.media_guid
                LEFT JOIN stream_flags sf ON sf.media_guid = mm.media_guid
                LEFT JOIN metadata_series_metadata sm ON sm.media_guid = mm.media_guid
                LEFT JOIN discovery_flags df ON df.platform = a.platform AND df.external_media_id = mm.external_media_id
            )
            """),
        ["StatisticsReadService.ListChannelsAsync.1"] = new(
            """
            WHERE @search = ''
               OR account_rollup.account_name ILIKE @search_pattern ESCAPE '\'
               OR account_rollup.account_handle ILIKE @search_pattern ESCAPE '\'
               OR account_rollup.platform ILIKE @search_pattern ESCAPE '\'
            """,
            """
            WHERE @search = ''
               OR fs_ilike(account_rollup.account_name, @search_pattern, '\')
               OR fs_ilike(account_rollup.account_handle, @search_pattern, '\')
               OR fs_ilike(account_rollup.platform, @search_pattern, '\')
            """),
        ["StatisticsReadService.ListChannelsAsync.2"] = new(
            """
            {fs0}
            ORDER BY {fs1}
            LIMIT @limit OFFSET @offset
            """,
            """
            {fs0}
            ORDER BY {fs1}
            LIMIT @limit OFFSET @offset
            """),
        ["StatisticsReadService.SuggestChannelsAsync.1"] = new(
            """
            WHERE account_rollup.account_name ILIKE @contains_pattern ESCAPE '\'
               OR account_rollup.account_handle ILIKE @contains_pattern ESCAPE '\'
               OR account_rollup.platform ILIKE @contains_pattern ESCAPE '\'
            ORDER BY
                CASE
                    WHEN account_rollup.account_name ILIKE @prefix_pattern ESCAPE '\' THEN 0
                    WHEN account_rollup.account_handle ILIKE @prefix_pattern ESCAPE '\' THEN 1
                    ELSE 2
                END,
                account_rollup.available_count DESC,
                COALESCE(account_rollup.account_name, account_rollup.account_handle, account_rollup.platform) ASC,
                account_rollup.account_id ASC
            LIMIT @limit
            """,
            """
            WHERE fs_ilike(account_rollup.account_name, @contains_pattern, '\')
               OR fs_ilike(account_rollup.account_handle, @contains_pattern, '\')
               OR fs_ilike(account_rollup.platform, @contains_pattern, '\')
            ORDER BY
                CASE
                    WHEN fs_ilike(account_rollup.account_name, @prefix_pattern, '\') THEN 0
                    WHEN fs_ilike(account_rollup.account_handle, @prefix_pattern, '\') THEN 1
                    ELSE 2
                END,
                account_rollup.available_count DESC,
                COALESCE(account_rollup.account_name, account_rollup.account_handle, account_rollup.platform) ASC,
                account_rollup.account_id ASC
            LIMIT @limit
            """),
        ["StatisticsReadService.GetChannelAsync.1"] = new(
            """
            WHERE source_rollup.creator_source_id = @creator_source_id
            """,
            """
            WHERE source_rollup.creator_source_id = @creator_source_id
            """),
        ["StatisticsReadService.GetChannelByAccountAsync.1"] = new(
            """
            WHERE account_rollup.account_id = @account_id
            """,
            """
            WHERE account_rollup.account_id = @account_id
            """),
        ["StatisticsReadService.GetDownloadHistoryAsync.1"] = new(
            """
            WITH bounds AS (
                SELECT
                    (@from AT TIME ZONE 'UTC')::date AS from_day,
                    (@to AT TIME ZONE 'UTC')::date AS to_day
            ),
            buckets AS (
                SELECT
                    series::date AS bucket_start,
                    LEAST((series + @step::interval)::date, bounds.to_day + 1) AS bucket_end
                FROM bounds,
                     generate_series(bounds.from_day::timestamp, bounds.to_day::timestamp, @step::interval) AS series
            )
            SELECT
                (b.bucket_start::timestamp AT TIME ZONE 'UTC') AS bucket_start,
                (b.bucket_end::timestamp AT TIME ZONE 'UTC') AS bucket_end,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'created'), 0) AS created,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'completed'), 0) AS completed,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state IN ('failed_transient', 'failed_permanent', 'dead_lettered')), 0) AS failed,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'cancelled'), 0) AS cancelled,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'ignored'), 0) AS ignored,
                COALESCE(SUM(a.bytes) FILTER (WHERE a.state = 'completed'), 0)::bigint AS bytes_completed,
                COALESCE(SUM(a.duration_seconds) FILTER (WHERE a.state = 'completed'), 0) AS duration_completed_seconds
            FROM buckets b
            LEFT JOIN statistics.download_daily_activity a
                ON a.day >= b.bucket_start AND a.day < b.bucket_end
            GROUP BY b.bucket_start, b.bucket_end
            ORDER BY b.bucket_start
            """,
            """
            WITH RECURSIVE bounds AS (
                SELECT date(@from / 1000000 - CASE WHEN @from % 1000000 < 0 THEN 1 ELSE 0 END, 'unixepoch') AS from_day,
                date(@to / 1000000 - CASE WHEN @to % 1000000 < 0 THEN 1 ELSE 0 END, 'unixepoch') AS to_day
            ), series(day) AS (
                SELECT from_day FROM bounds WHERE from_day <= to_day
                UNION ALL SELECT date(day, '+' || CASE @step WHEN '1 week' THEN '7 days' ELSE @step END, 'floor') FROM series, bounds WHERE date(day, '+' || CASE @step WHEN '1 week' THEN '7 days' ELSE @step END, 'floor') <= to_day
            ), buckets AS (
                SELECT day AS bucket_start, min(date(day, '+' || CASE @step WHEN '1 week' THEN '7 days' ELSE @step END, 'floor'), date(bounds.to_day, '+1 day')) AS bucket_end FROM series, bounds
            )
            SELECT
                CAST(strftime('%s', b.bucket_start) AS INTEGER) * 1000000 AS bucket_start,
                CAST(strftime('%s', b.bucket_end) AS INTEGER) * 1000000 AS bucket_end,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'created'), 0) AS created,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'completed'), 0) AS completed,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state IN ('failed_transient', 'failed_permanent', 'dead_lettered')), 0) AS failed,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'cancelled'), 0) AS cancelled,
                COALESCE(SUM(a.job_count) FILTER (WHERE a.state = 'ignored'), 0) AS ignored,
                COALESCE(SUM(a.bytes) FILTER (WHERE a.state = 'completed'), 0) AS bytes_completed,
                COALESCE(SUM(a.duration_seconds) FILTER (WHERE a.state = 'completed'), 0) AS duration_completed_seconds
            FROM buckets b
            LEFT JOIN statistics_download_daily_activity a
                ON a.day >= b.bucket_start AND a.day < b.bucket_end
            GROUP BY b.bucket_start, b.bucket_end
            ORDER BY b.bucket_start
            """),
        ["StatisticsReadService.GetCoverageSummaryAsync.1"] = new(
            """
            SELECT
                COUNT(*) FILTER (WHERE discovery_status NOT IN {fs0}) AS available_count,
                COUNT(*) FILTER (WHERE discovery_status IN ('Unavailable', 'PossiblyUnavailable')) AS unavailable_count,
                COUNT(*) FILTER (WHERE discovery_status = 'Ignored') AS ignored_count,
                COUNT(*) FILTER (WHERE discovery_status = 'RemovedFromSource') AS removed_count
            FROM discovery.discovered_media
            """,
            """
            SELECT
                COUNT(*) FILTER (WHERE discovery_status NOT IN {fs0}) AS available_count,
                COUNT(*) FILTER (WHERE discovery_status IN ('Unavailable', 'PossiblyUnavailable')) AS unavailable_count,
                COUNT(*) FILTER (WHERE discovery_status = 'Ignored') AS ignored_count,
                COUNT(*) FILTER (WHERE discovery_status = 'RemovedFromSource') AS removed_count
            FROM discovery_discovered_media
            """),
        ["StatisticsReadService.GetCoverageSummaryAsync.2"] = new(
            """
            SELECT
                platform,
                COUNT(*) FILTER (WHERE discovery_status NOT IN {fs0}) AS available_count
            FROM discovery.discovered_media
            GROUP BY platform
            ORDER BY available_count DESC, platform
            LIMIT 10
            """,
            """
            SELECT
                platform,
                COUNT(*) FILTER (WHERE discovery_status NOT IN {fs0}) AS available_count
            FROM discovery_discovered_media
            GROUP BY platform
            ORDER BY available_count DESC, platform
            LIMIT 10
            """),
        ["StatisticsReadService.GetInventoryAsync.1"] = new(
            """
            {fs0}
            SELECT
                (SELECT COUNT(*) FROM media.media) AS total_media,
                (SELECT COUNT(*) FROM metadata.accounts) AS total_channels,
                (SELECT COUNT(*) FROM discovery.creator_sources) AS total_creator_sources,
                (SELECT COUNT(*) FROM jobs.playlists) AS total_playlists,
                COALESCE((SELECT SUM(job_count) FROM statistics.download_daily_activity WHERE state = 'created'), 0) AS total_downloads,
                COALESCE((SELECT SUM(bytes) FROM statistics.download_daily_activity WHERE state = 'completed'), 0)::bigint AS total_bytes,
                COALESCE((SELECT SUM(duration_seconds) FROM classified_media), 0) AS total_duration_seconds
            """,
            """
            {fs0}
            SELECT
                (SELECT COUNT(*) FROM media_media) AS total_media,
                (SELECT COUNT(*) FROM metadata_accounts) AS total_channels,
                (SELECT COUNT(*) FROM discovery_creator_sources) AS total_creator_sources,
                (SELECT COUNT(*) FROM jobs_playlists) AS total_playlists,
                COALESCE((SELECT SUM(job_count) FROM statistics_download_daily_activity WHERE state = 'created'), 0) AS total_downloads,
                COALESCE((SELECT SUM(bytes) FROM statistics_download_daily_activity WHERE state = 'completed'), 0) AS total_bytes,
                COALESCE((SELECT SUM(duration_seconds) FROM classified_media), 0) AS total_duration_seconds
            """),
        ["StatisticsReadService.GetMediaTypesAsync.1"] = new(
            """
            {fs0}
            SELECT
                media_type,
                COUNT(*) AS count,
                COALESCE(SUM(duration_seconds), 0) AS duration_seconds,
                COALESCE(SUM(size_bytes), 0)::bigint AS bytes
            FROM classified_media
            GROUP BY media_type
            ORDER BY count DESC, media_type
            """,
            """
            {fs0}
            SELECT
                media_type,
                COUNT(*) AS count,
                COALESCE(SUM(duration_seconds), 0) AS duration_seconds,
                COALESCE(SUM(size_bytes), 0) AS bytes
            FROM classified_media
            GROUP BY media_type
            ORDER BY count DESC, media_type
            """),
        ["StatisticsReadService.GetDownloadStatesAsync.1"] = new(
            """
            SELECT state::text AS state, COUNT(*) AS count
            FROM jobs.download_jobs
            GROUP BY state
            ORDER BY count DESC, state
            """,
            """
            SELECT state AS state, COUNT(*) AS count
            FROM jobs_download_jobs
            GROUP BY state
            ORDER BY count DESC, state
            """),
        ["StatisticsReadService.GetWatchStatisticsAsync.1"] = new(
            """
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
            """,
            """
            SELECT
                COUNT(*) FILTER (WHERE completed) AS watched_count,
                COALESCE(SUM(
                    CASE
                        WHEN position_seconds IS NULL THEN 0
                        WHEN duration_seconds IS NULL THEN position_seconds
                        ELSE min(position_seconds, duration_seconds)
                    END), 0) AS watch_progress_seconds
            FROM media_watch_states
            WHERE owner_subject = @owner_subject
            """),
        ["StatisticsReadService.GetChannelWithMediaCountAsync.1"] = new(
            """
            SELECT COUNT(DISTINCT mm.account_id)
            FROM metadata.media_metadata mm
            JOIN metadata.accounts a ON a.id = mm.account_id
            WHERE @search = ''
               OR a.account_name ILIKE @search_pattern ESCAPE '\'
               OR a.account_handle ILIKE @search_pattern ESCAPE '\'
               OR a.platform ILIKE @search_pattern ESCAPE '\'
            """,
            """
            SELECT COUNT(DISTINCT mm.account_id)
            FROM metadata_media_metadata mm
            JOIN metadata_accounts a ON a.id = mm.account_id
            WHERE @search = ''
               OR fs_ilike(a.account_name, @search_pattern, '\')
               OR fs_ilike(a.account_handle, @search_pattern, '\')
               OR fs_ilike(a.platform, @search_pattern, '\')
            """),
        ["StatisticsReadService.AccountSummarySql.1"] = new(
            """
            {fs0},
            downloaded_guids AS (
                SELECT DISTINCT media_guid
                FROM statistics.channel_media_downloads
            ),
            account_rollup AS (
                SELECT
                    a.id AS account_id,
                    a.platform,
                    a.account_name,
                    a.account_handle,
                    a.avatar_storage_path,
                    COUNT(cm.media_guid) AS available_count,
                    COUNT(cm.media_guid) FILTER (WHERE dg.media_guid IS NOT NULL) AS downloaded_count,
                    COALESCE(SUM(cm.duration_seconds), 0) AS total_duration_seconds,
                    COALESCE(SUM(cm.duration_seconds) FILTER (WHERE dg.media_guid IS NOT NULL), 0) AS downloaded_duration_seconds,
                    COALESCE(SUM(cm.size_bytes), 0)::bigint AS total_bytes
                FROM metadata.accounts a
                JOIN classified_media cm ON cm.account_id = a.id
                LEFT JOIN downloaded_guids dg ON dg.media_guid = cm.media_guid
                GROUP BY a.id, a.platform, a.account_name, a.account_handle, a.avatar_storage_path
            )
            SELECT
                source_link.creator_source_id,
                account_rollup.platform,
                source_link.source_type,
                source_link.source_url,
                account_rollup.account_id,
                account_rollup.account_name,
                account_rollup.account_handle,
                account_rollup.avatar_storage_path,
                account_rollup.available_count,
                account_rollup.downloaded_count,
                account_rollup.total_duration_seconds,
                account_rollup.downloaded_duration_seconds,
                account_rollup.total_bytes,
                source_link.last_successful_scan_at,
                source_link.last_full_scan_at
            FROM account_rollup
            LEFT JOIN LATERAL (
                SELECT
                    cs.id AS creator_source_id,
                    cs.source_type,
                    cs.source_url,
                    css.last_successful_scan_at,
                    css.last_full_scan_at
                FROM discovery.discovered_media dm
                JOIN metadata.media_metadata mm ON mm.external_media_id = dm.external_media_id
                JOIN discovery.creator_sources cs ON cs.id = dm.creator_source_id AND cs.platform = account_rollup.platform
                LEFT JOIN jobs.creator_scan_state css ON css.creator_source_id = cs.id
                WHERE mm.account_id = account_rollup.account_id
                GROUP BY cs.id, cs.source_type, cs.source_url, css.last_successful_scan_at, css.last_full_scan_at
                ORDER BY css.last_successful_scan_at DESC NULLS LAST, cs.id
                LIMIT 1
            ) source_link ON true
            {fs1}
            """,
            """
            {fs0},
            downloaded_guids AS (
                SELECT DISTINCT media_guid
                FROM statistics_channel_media_downloads
            ),
            account_rollup AS (
                SELECT
                    a.id AS account_id,
                    a.platform,
                    a.account_name,
                    a.account_handle,
                    a.avatar_storage_path,
                    COUNT(cm.media_guid) AS available_count,
                    COUNT(cm.media_guid) FILTER (WHERE dg.media_guid IS NOT NULL) AS downloaded_count,
                    COALESCE(SUM(cm.duration_seconds), 0) AS total_duration_seconds,
                    COALESCE(SUM(cm.duration_seconds) FILTER (WHERE dg.media_guid IS NOT NULL), 0) AS downloaded_duration_seconds,
                    COALESCE(SUM(cm.size_bytes), 0) AS total_bytes
                FROM metadata_accounts a
                JOIN classified_media cm ON cm.account_id = a.id
                LEFT JOIN downloaded_guids dg ON dg.media_guid = cm.media_guid
                GROUP BY a.id, a.platform, a.account_name, a.account_handle, a.avatar_storage_path
            )
            SELECT
                source_link.creator_source_id,
                account_rollup.platform,
                source_link.source_type,
                source_link.source_url,
                account_rollup.account_id,
                account_rollup.account_name,
                account_rollup.account_handle,
                account_rollup.avatar_storage_path,
                account_rollup.available_count,
                account_rollup.downloaded_count,
                account_rollup.total_duration_seconds,
                account_rollup.downloaded_duration_seconds,
                account_rollup.total_bytes,
                source_link.last_successful_scan_at,
                source_link.last_full_scan_at
            FROM account_rollup
            LEFT JOIN (
                SELECT
                    mm.account_id, a.platform AS linked_platform, row_number() OVER (PARTITION BY mm.account_id ORDER BY css.last_successful_scan_at DESC NULLS LAST, cs.id) AS ordinal,
                    cs.id AS creator_source_id,
                    cs.source_type,
                    cs.source_url,
                    css.last_successful_scan_at,
                    css.last_full_scan_at
                FROM discovery_discovered_media dm
                JOIN metadata_media_metadata mm ON mm.external_media_id = dm.external_media_id
                JOIN metadata_accounts a ON a.id = mm.account_id
                JOIN discovery_creator_sources cs ON cs.id = dm.creator_source_id AND cs.platform = a.platform
                LEFT JOIN jobs_creator_scan_state css ON css.creator_source_id = cs.id
                GROUP BY mm.account_id, a.platform, cs.id, cs.source_type, cs.source_url, css.last_successful_scan_at, css.last_full_scan_at
            ) source_link ON source_link.account_id = account_rollup.account_id AND source_link.ordinal = 1
            {fs1}
            """),
        ["StatisticsReadService.ChannelSummarySql.1"] = new(
            """
            {fs0},
            source_downloaded_media AS (
                SELECT DISTINCT
                    dm.creator_source_id,
                    cm.media_guid,
                    cm.duration_seconds,
                    cm.size_bytes
                FROM discovery.discovered_media dm
                JOIN classified_media cm ON cm.platform = dm.platform AND cm.external_media_id = dm.external_media_id
                JOIN statistics.channel_media_downloads cmd ON cmd.media_guid = cm.media_guid
            ),
            source_rollup AS (
                SELECT
                    cs.id AS creator_source_id,
                    cs.platform,
                    cs.source_type,
                    cs.source_url,
                    css.last_successful_scan_at,
                    css.last_full_scan_at,
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
                LEFT JOIN jobs.creator_scan_state css ON css.creator_source_id = cs.id
                LEFT JOIN discovery.discovered_media dm ON dm.creator_source_id = cs.id
                LEFT JOIN LATERAL (
                    SELECT
                        COUNT(*) AS downloaded_count,
                        COALESCE(SUM(duration_seconds), 0) AS downloaded_duration_seconds,
                        COALESCE(SUM(size_bytes), 0)::bigint AS total_bytes
                    FROM source_downloaded_media sdm
                    WHERE sdm.creator_source_id = cs.id
                ) downloaded_rollup ON true
                GROUP BY cs.id, cs.platform, cs.source_type, cs.source_url, css.last_successful_scan_at, css.last_full_scan_at
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
            {fs1}
            """,
            """
            {fs0},
            source_downloaded_media AS (
                SELECT DISTINCT
                    dm.creator_source_id,
                    cm.media_guid,
                    cm.duration_seconds,
                    cm.size_bytes
                FROM discovery_discovered_media dm
                JOIN classified_media cm ON cm.platform = dm.platform AND cm.external_media_id = dm.external_media_id
                JOIN statistics_channel_media_downloads cmd ON cmd.media_guid = cm.media_guid
            ),
            source_rollup AS (
                SELECT
                    cs.id AS creator_source_id,
                    cs.platform,
                    cs.source_type,
                    cs.source_url,
                    css.last_successful_scan_at,
                    css.last_full_scan_at,
                    COUNT(dm.id) FILTER (
                        WHERE dm.discovery_status NOT IN ('Ignored', 'Unavailable', 'RemovedFromSource')
                    ) AS available_count,
                    COALESCE(downloaded_rollup.downloaded_count, 0) AS downloaded_count,
                    COALESCE(SUM(dm.duration_seconds) FILTER (
                        WHERE dm.discovery_status NOT IN ('Ignored', 'Unavailable', 'RemovedFromSource')
                    ), 0) AS total_duration_seconds,
                    COALESCE(downloaded_rollup.downloaded_duration_seconds, 0) AS downloaded_duration_seconds,
                    COALESCE(downloaded_rollup.total_bytes, 0) AS total_bytes
                FROM discovery_creator_sources cs
                LEFT JOIN jobs_creator_scan_state css ON css.creator_source_id = cs.id
                LEFT JOIN discovery_discovered_media dm ON dm.creator_source_id = cs.id
                LEFT JOIN (
                    SELECT sdm.creator_source_id,
                        COUNT(*) AS downloaded_count,
                        COALESCE(SUM(duration_seconds), 0) AS downloaded_duration_seconds,
                        COALESCE(SUM(size_bytes), 0) AS total_bytes
                    FROM source_downloaded_media sdm
                    GROUP BY sdm.creator_source_id
                ) downloaded_rollup ON downloaded_rollup.creator_source_id = cs.id
                GROUP BY cs.id, cs.platform, cs.source_type, cs.source_url, css.last_successful_scan_at, css.last_full_scan_at
                    , downloaded_rollup.downloaded_count, downloaded_rollup.downloaded_duration_seconds, downloaded_rollup.total_bytes
            )
            SELECT
                source_rollup.*,
                CASE WHEN available_count = 0 THEN 0 ELSE CAST(downloaded_count AS REAL) * 100 / available_count END AS downloaded_percent,
                account_rollup.account_id,
                account_rollup.account_name,
                account_rollup.account_handle,
                account_rollup.avatar_storage_path
            FROM source_rollup
            LEFT JOIN (
                SELECT dm.creator_source_id, row_number() OVER (PARTITION BY dm.creator_source_id ORDER BY COUNT(*) DESC, a.id) AS ordinal,
                    a.id AS account_id,
                    a.account_name,
                    a.account_handle,
                    a.avatar_storage_path,
                    COUNT(*) AS linked_count
                FROM discovery_discovered_media dm
                JOIN metadata_media_metadata mm ON mm.external_media_id = dm.external_media_id
                JOIN metadata_accounts a ON a.id = mm.account_id AND a.platform = dm.platform
                GROUP BY dm.creator_source_id, a.id, a.account_name, a.account_handle, a.avatar_storage_path
            ) account_rollup ON account_rollup.creator_source_id = source_rollup.creator_source_id AND account_rollup.ordinal = 1
            {fs1}
            """),
        ["StatisticsReadService.GetChannelStatusCountsAsync.1"] = new(
            """
            SELECT discovery_status, COUNT(*) AS count
            FROM discovery.discovered_media
            WHERE creator_source_id = @creator_source_id
            GROUP BY discovery_status
            """,
            """
            SELECT discovery_status, COUNT(*) AS count
            FROM discovery_discovered_media
            WHERE creator_source_id = @creator_source_id
            GROUP BY discovery_status
            """),
        ["StatisticsReadService.GetChannelMediaTypesAsync.1"] = new(
            """
            {fs0}
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
            """,
            """
            {fs0}
            SELECT
                cm.media_type,
                COUNT(*) AS count,
                COALESCE(SUM(cm.duration_seconds), 0) AS duration_seconds,
                COALESCE(SUM(cm.size_bytes), 0) AS bytes
            FROM (
                SELECT DISTINCT
                    cm.media_guid,
                    cm.media_type,
                    cm.duration_seconds,
                    cm.size_bytes
                FROM discovery_discovered_media dm
                JOIN classified_media cm ON cm.platform = dm.platform AND cm.external_media_id = dm.external_media_id
                WHERE dm.creator_source_id = @creator_source_id
            ) cm
            GROUP BY cm.media_type
            ORDER BY count DESC, cm.media_type
            """),
        ["StatisticsReadService.GetChannelDownloadStatesAsync.1"] = new(
            """
            SELECT state, SUM(job_count) AS count
            FROM statistics.creator_source_daily_states
            WHERE creator_source_id = @creator_source_id
            GROUP BY state
            ORDER BY count DESC, state
            """,
            """
            SELECT state, SUM(job_count) AS count
            FROM statistics_creator_source_daily_states
            WHERE creator_source_id = @creator_source_id
            GROUP BY state
            ORDER BY count DESC, state
            """),
        ["StatisticsReadService.GetAccountStatusCountsAsync.1"] = new(
            """
            SELECT dm.discovery_status, COUNT(DISTINCT dm.id) AS count
            FROM discovery.discovered_media dm
            JOIN metadata.media_metadata mm ON mm.external_media_id = dm.external_media_id
            JOIN metadata.accounts a ON a.id = mm.account_id AND a.platform = dm.platform
            WHERE mm.account_id = @account_id
            GROUP BY dm.discovery_status
            """,
            """
            SELECT dm.discovery_status, COUNT(DISTINCT dm.id) AS count
            FROM discovery_discovered_media dm
            JOIN metadata_media_metadata mm ON mm.external_media_id = dm.external_media_id
            JOIN metadata_accounts a ON a.id = mm.account_id AND a.platform = dm.platform
            WHERE mm.account_id = @account_id
            GROUP BY dm.discovery_status
            """),
        ["StatisticsReadService.GetAccountMediaTypesAsync.1"] = new(
            """
            {fs0}
            SELECT
                cm.media_type,
                COUNT(*) AS count,
                COALESCE(SUM(cm.duration_seconds), 0) AS duration_seconds,
                COALESCE(SUM(cm.size_bytes), 0)::bigint AS bytes
            FROM classified_media cm
            WHERE cm.account_id = @account_id
            GROUP BY cm.media_type
            ORDER BY count DESC, cm.media_type
            """,
            """
            {fs0}
            SELECT
                cm.media_type,
                COUNT(*) AS count,
                COALESCE(SUM(cm.duration_seconds), 0) AS duration_seconds,
                COALESCE(SUM(cm.size_bytes), 0) AS bytes
            FROM classified_media cm
            WHERE cm.account_id = @account_id
            GROUP BY cm.media_type
            ORDER BY count DESC, cm.media_type
            """),
        ["StatisticsReadService.GetAccountDownloadStatesAsync.1"] = new(
            """
            SELECT state, SUM(job_count) AS count
            FROM statistics.account_daily_states
            WHERE account_id = @account_id
            GROUP BY state
            ORDER BY count DESC, state
            """,
            """
            SELECT state, SUM(job_count) AS count
            FROM statistics_account_daily_states
            WHERE account_id = @account_id
            GROUP BY state
            ORDER BY count DESC, state
            """),
        ["DownloadStatisticsRecorder.RecordDailyActivityAsync.1"] = new(
            """
            INSERT INTO statistics.download_daily_activity (day, state, job_count, bytes, duration_seconds)
            VALUES ({fs0}, {fs1}, 1, {fs2}, {fs3})
            ON CONFLICT (day, state) DO UPDATE SET
                job_count = statistics.download_daily_activity.job_count + 1,
                bytes = statistics.download_daily_activity.bytes + EXCLUDED.bytes,
                duration_seconds = statistics.download_daily_activity.duration_seconds + EXCLUDED.duration_seconds
            """,
            """
            INSERT INTO statistics_download_daily_activity (day, state, job_count, bytes, duration_seconds)
            VALUES ({fs0}, {fs1}, 1, {fs2}, {fs3})
            ON CONFLICT (day, state) DO UPDATE SET
                job_count = statistics_download_daily_activity.job_count + 1,
                bytes = statistics_download_daily_activity.bytes + EXCLUDED.bytes,
                duration_seconds = statistics_download_daily_activity.duration_seconds + EXCLUDED.duration_seconds
            """),
        ["DownloadStatisticsRecorder.RecordChannelDailyStatesAsync.1"] = new(
            """
            INSERT INTO statistics.creator_source_daily_states (day, creator_source_id, state, job_count)
            SELECT {fs0}, dm.creator_source_id, {fs1}, 1
            FROM discovery.discovered_media dm
            WHERE dm.canonical_url = {fs2}
            LIMIT 1
            ON CONFLICT (day, creator_source_id, state) DO UPDATE SET
                job_count = statistics.creator_source_daily_states.job_count + 1
            """,
            """
            INSERT INTO statistics_creator_source_daily_states (day, creator_source_id, state, job_count)
            SELECT {fs0}, dm.creator_source_id, {fs1}, 1
            FROM discovery_discovered_media dm
            WHERE dm.canonical_url = {fs2}
            LIMIT 1
            ON CONFLICT (day, creator_source_id, state) DO UPDATE SET
                job_count = statistics_creator_source_daily_states.job_count + 1
            """),
        ["DownloadStatisticsRecorder.RecordChannelDailyStatesAsync.2"] = new(
            """
            INSERT INTO statistics.account_daily_states (day, account_id, state, job_count)
            SELECT {fs0}, mm.account_id, {fs1}, 1
            FROM metadata.media_metadata mm
            WHERE mm.webpage_url = {fs2}
            LIMIT 1
            ON CONFLICT (day, account_id, state) DO UPDATE SET
                job_count = statistics.account_daily_states.job_count + 1
            """,
            """
            INSERT INTO statistics_account_daily_states (day, account_id, state, job_count)
            SELECT {fs0}, mm.account_id, {fs1}, 1
            FROM metadata_media_metadata mm
            WHERE mm.webpage_url = {fs2}
            LIMIT 1
            ON CONFLICT (day, account_id, state) DO UPDATE SET
                job_count = statistics_account_daily_states.job_count + 1
            """),
        ["DownloadStatisticsRecorder.RecordCompletionStatisticsAsync.1"] = new(
            """
            SELECT COALESCE(mm.duration, 0) AS "Value"
            FROM metadata.media_metadata mm
            WHERE mm.media_guid = {fs0}
            """,
            """
            SELECT COALESCE(mm.duration, 0) AS "Value"
            FROM metadata_media_metadata mm
            WHERE mm.media_guid = {fs0}
            """),
        ["DownloadStatisticsRecorder.RecordCompletionStatisticsAsync.2"] = new(
            """
            INSERT INTO statistics.channel_media_downloads
                (media_guid, account_id, platform, bytes, duration_seconds, completed_at)
            SELECT mm.media_guid, mm.account_id, a.platform, {fs0}, COALESCE(mm.duration, 0), {fs1}
            FROM metadata.media_metadata mm
            JOIN metadata.accounts a ON a.id = mm.account_id
            WHERE mm.media_guid = {fs2}
            """,
            """
            INSERT INTO statistics_channel_media_downloads
                (media_guid, account_id, platform, bytes, duration_seconds, completed_at)
            SELECT mm.media_guid, mm.account_id, a.platform, {fs0}, COALESCE(mm.duration, 0), {fs1}
            FROM metadata_media_metadata mm
            JOIN metadata_accounts a ON a.id = mm.account_id
            WHERE mm.media_guid = {fs2}
            """),
    };
}
