using DataBridge.Persistence;
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
            await db.Database.ExecuteSqlInterpolatedAsync(db.ParameterizedSql("DownloadStatisticsRecorder.RecordDailyActivityAsync.1", day, state, bytes, durationSeconds), ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed recording daily activity statistics for state {State}.", state);
        }
    }

    /// <summary>
    /// Best-effort per-channel echo of <see cref="RecordDailyActivityAsync"/>: resolves the owning
    /// creator source (via discovery.discovered_media, present before a download even starts) and
    /// account (via metadata.media_metadata, present once metadata has been fetched) at the moment a
    /// job reaches a state, so the channel-detail "recent download states" card survives
    /// download_jobs retention instead of depending on a job row DownloadHistoryPurger deletes after
    /// ~30 days. A source URL that matches neither (ad-hoc downloads that failed before metadata was
    /// fetched) simply records nothing — there is no channel to attribute it to yet.
    /// </summary>
    public static async Task RecordChannelDailyStatesAsync(
        DataBridgeDbContext db, ILogger? logger, string? sourceUrl, string state, Instant occurredAt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return;

        var day = occurredAt.InUtc().Date;

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(db.ParameterizedSql("DownloadStatisticsRecorder.RecordChannelDailyStatesAsync.1", day, state, sourceUrl), ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed recording creator-source daily download state for state {State}.", state);
        }

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(db.ParameterizedSql("DownloadStatisticsRecorder.RecordChannelDailyStatesAsync.2", day, state, sourceUrl), ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed recording account daily download state for state {State}.", state);
        }
    }

    /// <summary>
    /// Records both the global "completed" daily rollup and the per-channel/per-media ledger row for
    /// a genuinely finalized download. Looks the primary artifact's size up from jobs.download_artifacts
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

            // SqlQuery only supports scalar element types, so the duration comes back on its own and
            // the ledger row resolves its owning account inline — a missing metadata row then simply
            // inserts nothing instead of costing us the daily rollup too.
            var durationSeconds = await db.Database.SqlQuery<double>(db.ParameterizedSql("DownloadStatisticsRecorder.RecordCompletionStatisticsAsync.1", mediaGuid)).FirstOrDefaultAsync(ct);

            await RecordDailyActivityAsync(db, logger, "completed", completedAt, bytes, durationSeconds, ct);

            var ledgerRows = await db.Database.ExecuteSqlInterpolatedAsync(db.ParameterizedSql("DownloadStatisticsRecorder.RecordCompletionStatisticsAsync.2", bytes, completedAt, mediaGuid), ct);

            if (ledgerRows == 0)
            {
                logger?.LogWarning(
                    "No owning account found for MediaGuid {MediaGuid}; channel download statistics were not recorded.",
                    mediaGuid);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed recording completion statistics for MediaGuid {MediaGuid}.", mediaGuid);
        }
    }
}
