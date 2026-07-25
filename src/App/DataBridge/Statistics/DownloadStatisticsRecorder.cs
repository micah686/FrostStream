using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Statistics;

/// <summary>
/// download_jobs rows are periodically cleaned up, so lifetime/history statistics can't be derived
/// from them long-term. These record into the durable `statistics` schema instead, at the same
/// points a job row is created or reaches a terminal state (from DownloadFlowV2Repository and
/// PlaylistsRepository, the two places job rows get created). Best-effort: a statistics write
/// failing must never fail the job-state transition that triggered it.
/// </summary>
internal static class DownloadStatisticsRecorder
{
    public static async Task RecordDailyActivityAsync(
        DataBridgeDbContext db, ILogger? logger, string state, Instant occurredAt,
        long bytes, double durationSeconds, CancellationToken ct)
    {
        try
        {
            var day = occurredAt.InUtc().Date;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO statistics.download_daily_activity (day, state, job_count, bytes, duration_seconds)
                VALUES ({day}, {state}, 1, {bytes}, {durationSeconds})
                ON CONFLICT (day, state) DO UPDATE SET
                    job_count = statistics.download_daily_activity.job_count + 1,
                    bytes = statistics.download_daily_activity.bytes + EXCLUDED.bytes,
                    duration_seconds = statistics.download_daily_activity.duration_seconds + EXCLUDED.duration_seconds
                """, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed recording daily activity statistics for state {State}.", state);
        }
    }

    /// <summary>
    /// Records both the global "completed" daily rollup and the per-channel/per-media ledger row for
    /// a genuinely finalized download. Looks the primary artifact's size up from downloads.download_artifacts
    /// (not job.FileSizeBytes, which the V2 flow never populates) and the owning account/duration from
    /// metadata so both survive independent of download_jobs retention.
    /// </summary>
    public static async Task RecordCompletionStatisticsAsync(
        DataBridgeDbContext db, ILogger? logger, Guid mediaGuid, Guid runId, Instant completedAt, CancellationToken ct)
    {
        try
        {
            var bytes = await db.DownloadArtifacts.AsNoTracking()
                .Where(x => x.RunId == runId && x.Kind == UploadArtifactKind.Primary)
                .Select(x => x.SizeBytes)
                .FirstOrDefaultAsync(ct) ?? 0;

            var owner = await db.Database.SqlQuery<MediaOwnerRow>($"""
                SELECT a.platform AS Platform, mm.account_id AS AccountId, COALESCE(mm.duration, 0) AS DurationSeconds
                FROM metadata.media_metadata mm
                JOIN metadata.accounts a ON a.id = mm.account_id
                WHERE mm.media_guid = {mediaGuid}
                """).FirstOrDefaultAsync(ct);

            await RecordDailyActivityAsync(db, logger, "completed", completedAt, bytes, owner?.DurationSeconds ?? 0, ct);

            if (owner is null)
                return;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO statistics.channel_media_downloads
                    (media_guid, account_id, platform, bytes, duration_seconds, completed_at)
                VALUES ({mediaGuid}, {owner.AccountId}, {owner.Platform}, {bytes}, {owner.DurationSeconds}, {completedAt})
                """, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed recording completion statistics for MediaGuid {MediaGuid}.", mediaGuid);
        }
    }

    private sealed class MediaOwnerRow
    {
        public required string Platform { get; init; }
        public long AccountId { get; init; }
        public double DurationSeconds { get; init; }
    }
}
