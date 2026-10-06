using DataBridge.Data;
using DataBridge.Messaging;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Persistence;

/// <summary>Application retention only. Durable workflow inspection/deletion remains a separate store contract.</summary>
public sealed class ApplicationRetention(DataBridgeDbContext db)
{
    private static IQueryable<DownloadJobEntity> EligibleJobs(DataBridgeDbContext db, Instant cutoff,
        DownloadJobStatus[] jobs, DownloadGroupStatus[] groups, DownloadJobStatus[] blocking)
        => db.DownloadJobs.Where(job => jobs.Contains(job.Status)
            && (job.CompletedAt ?? job.UpdatedAt) < cutoff
            && db.DownloadGroups.Any(group => group.CorrelationId == job.CorrelationId && groups.Contains(group.Status)
                && (group.CompletedAt ?? group.UpdatedAt) < cutoff)
            && !db.DownloadJobs.Any(sibling => sibling.CorrelationId == job.CorrelationId && blocking.Contains(sibling.Status))
            && !db.DownloadWorkerLeases.Any(lease => lease.JobId == job.JobId && lease.Status == DownloadWorkerLeaseStatus.Active));

    public async Task<List<(Guid JobId, Guid CorrelationId)>> SelectJobsAsync(Instant cutoff,
        DownloadJobStatus[] jobs, DownloadGroupStatus[] groups, DownloadJobStatus[] blocking, CancellationToken ct)
    {
        var rows = await EligibleJobs(db, cutoff, jobs, groups, blocking).AsNoTracking().OrderBy(x => x.JobId)
            .Take(ApplicationBatches.WriteBatchSize).Select(x => new { x.JobId, x.CorrelationId }).ToListAsync(ct);
        return rows.Select(x => (x.JobId, x.CorrelationId)).ToList();
    }

    public async Task<int> DeleteJobsAsync(Guid[] ids, Instant cutoff, DownloadJobStatus[] jobs,
        DownloadGroupStatus[] groups, DownloadJobStatus[] blocking, CancellationToken ct)
    {
        var total = 0;
        foreach (var chunk in ids.Distinct().Chunk(ApplicationBatches.MembershipBatchSize))
            total += await db.MutateAsync("Retention.DeleteJobs", async () =>
            {
                // Recheck every eligibility predicate after acquiring writer ownership. A job may
                // have restarted while its old terminal workflow instances were being deleted.
                var eligible = await EligibleJobs(db, cutoff, jobs, groups, blocking)
                    .Where(x => chunk.Contains(x.JobId)).Select(x => x.JobId).ToArrayAsync(ct);
                if (eligible.Length == 0) return 0;
                var deleted = await db.DownloadJobs.Where(x => eligible.Contains(x.JobId)).ExecuteDeleteAsync(ct);
                await db.FailedDownloadJobs.Where(x => eligible.Contains(x.JobId)).ExecuteDeleteAsync(ct);
                await db.ProcessedMessages.Where(x => eligible.Contains(x.JobId)).ExecuteDeleteAsync(ct);
                return deleted;
            }, ct);
        return total;
    }

    public async Task<List<(Guid GroupId, Guid CorrelationId)>> SelectDrainedGroupsAsync(Guid[] correlationIds, CancellationToken ct)
    {
        var rows = await db.DownloadGroups.AsNoTracking()
            .Where(x => !db.DownloadJobs.Any(job => job.CorrelationId == x.CorrelationId))
            .WithIdsAsync(correlationIds, x => x.CorrelationId, ct);
        return rows.Select(x => (x.GroupId, x.CorrelationId)).ToList();
    }

    public async Task<int> DeleteGroupsAsync(Guid[] correlationIds, CancellationToken ct)
    {
        var total = 0;
        foreach (var chunk in correlationIds.Distinct().Chunk(ApplicationBatches.MembershipBatchSize))
            total += await db.MutateAsync("Retention.DeleteGroups", async () =>
            {
                var drained = await db.DownloadGroups.Where(x => chunk.Contains(x.CorrelationId)
                    && !db.DownloadJobs.Any(job => job.CorrelationId == x.CorrelationId))
                    .Select(x => x.CorrelationId).ToArrayAsync(ct);
                if (drained.Length == 0) return 0;
                await db.PlaylistScanEntries.Where(entry => db.Playlists.Any(playlist => playlist.PlaylistId == entry.PlaylistId
                    && drained.Contains(playlist.CorrelationId))).ExecuteDeleteAsync(ct);
                return await db.DownloadGroups.Where(x => drained.Contains(x.CorrelationId)).ExecuteDeleteAsync(ct);
            }, ct);
        return total;
    }

    public async Task<List<(Guid JobId, Guid RunId)>> SelectRunsAsync(Guid[] jobIds, CancellationToken ct)
    {
        var rows = await db.DownloadJobRuns.AsNoTracking().Select(x => new { x.JobId, x.RunId }).WithIdsAsync(jobIds, x => x.JobId, ct);
        return rows.Select(x => (x.JobId, x.RunId)).ToList();
    }

    private static readonly ImportSessionStatus[] TerminalSessions =
        [ImportSessionStatus.ScanFailed, ImportSessionStatus.Completed, ImportSessionStatus.CompletedWithFailures, ImportSessionStatus.Cancelled];

    public Task<List<Guid>> SelectSessionsAsync(Instant cutoff, CancellationToken ct)
        => db.ImportSessions.AsNoTracking().Where(x => TerminalSessions.Contains(x.Status) && x.CompletedAt < cutoff)
            .OrderBy(x => x.SessionId).Take(ApplicationBatches.WriteBatchSize).Select(x => x.SessionId).ToListAsync(ct);

    public async Task<List<Guid>> SelectSessionItemsAsync(Guid[] sessionIds, CancellationToken ct)
        => (await db.ImportSessionItems.AsNoTracking().Select(x => new { x.SessionId, x.ItemId }).WithIdsAsync(sessionIds, x => x.SessionId, ct)).Select(x => x.ItemId).ToList();

    public async Task<int> DeleteSessionsAsync(Guid[] ids, Instant cutoff, CancellationToken ct)
    {
        var total = 0;
        foreach (var chunk in ids.Distinct().Chunk(ApplicationBatches.MembershipBatchSize))
            total += await db.MutateAsync("Retention.DeleteSessions", () => db.ImportSessions
                .Where(x => chunk.Contains(x.SessionId) && TerminalSessions.Contains(x.Status) && x.CompletedAt < cutoff)
                .ExecuteDeleteAsync(ct), ct);
        return total;
    }
    public Task<int> DeleteStaleMediaBatchAsync(CancellationToken ct)
        => db.MutateAsync("Retention.StaleMedia", async () =>
        {
            var active = DownloadJobStateSql.ActiveJobStates;
            var ids = await db.Media.Where(media => !db.MediaContentIdVersions.Any(version => version.MediaGuid == media.MediaGuid)
                && !db.MediaSourceVersions.Any(source => source.MediaGuid == media.MediaGuid
                    && db.DownloadJobs.Any(job => job.JobId == source.LatestJobId && active.Contains(job.State))))
                .OrderBy(x => x.MediaGuid).Take(ApplicationBatches.MembershipBatchSize).Select(x => x.MediaGuid).ToArrayAsync(ct);
            return ids.Length == 0 ? 0 : await db.Media.Where(x => ids.Contains(x.MediaGuid)).ExecuteDeleteAsync(ct);
        }, ct);

}
