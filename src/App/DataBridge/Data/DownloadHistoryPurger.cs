using Cleipnir.ResilientFunctions.Domain;
using DataBridge.Flows;
using DataBridge.Messaging;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed record DownloadHistoryCleanupResult
{
    public int RetentionDays { get; init; }
    public bool IncludeFailed { get; init; }
    public int PurgedJobs { get; init; }
    public int PurgedGroups { get; init; }
    public int DeletedJobFlows { get; init; }
    public int DeletedGroupFlows { get; init; }
    public int DeletedOrphanFlows { get; init; }
    public bool OrphanSweepTruncated { get; init; }

    public string Describe()
        => $"Deleted {PurgedJobs} job(s), {PurgedGroups} group(s), "
           + $"{DeletedJobFlows + DeletedGroupFlows} flow instance(s), and {DeletedOrphanFlows} orphaned flow(s)."
           + (OrphanSweepTruncated ? " The orphan sweep hit its per-run cap and will continue next run." : string.Empty);
}

public interface IDownloadHistoryPurger
{
    Task<DownloadHistoryCleanupResult> PurgeAsync(
        int retentionDays,
        bool includeFailed,
        Func<string, Task>? reportProgress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Deletes download-job history for work that has genuinely finished.
/// </summary>
/// <remarks>
/// <para>
/// Two invariants govern this class and must not be relaxed.
/// </para>
/// <para>
/// <b>Flows are deleted before the rows that name them.</b> There is no referential relationship
/// between the <c>jobs</c> schema and the <c>cleipnir</c> schema — the only link is the instance-id
/// string convention in <see cref="DownloadFlowInstance"/>, stored in
/// <c>cleipnir.flows.human_instance_id</c>. Every existing flow-deletion path constructs that id from
/// a SQL row, so deleting the row first leaves a flow nobody can name, and therefore nobody can ever
/// delete. If the process dies between the flow delete and the row delete the rows simply survive and
/// the next run retries, so this ordering is also the crash-safe one.
/// </para>
/// <para>
/// <b>Failed and stopped jobs are preserved unless explicitly opted into.</b>
/// <c>DownloadFlowV2Repository.StartFreshRunAsync</c> rebuilds the original request by deserializing
/// the earliest <c>jobs.download_job_history</c> row with <c>event_name = 'DownloadRequested'</c>.
/// That history cascades away with the job row, so purging a restartable job silently and permanently
/// destroys the ability to retry it.
/// </para>
/// </remarks>
public sealed class DownloadHistoryPurger(
    NpgsqlDataSource dataSource,
    DownloadJobV2Flows jobFlows,
    DownloadGroupV2Flows groupFlows,
    LocalImportItemV2Flows importFlows,
    IClock clock,
    ILogger<DownloadHistoryPurger> logger) : IDownloadHistoryPurger
{
    public const int DefaultRetentionDays = 30;

    /// <summary>Jobs are purged a batch at a time so a first run on a long-lived install never holds one huge transaction.</summary>
    private const int BatchSize = 500;

    /// <summary>Ceiling on how many terminal flow rows one run inspects, so the sweep cannot run away.</summary>
    private const int OrphanSweepLimit = 20_000;

    /// <summary>A job in any of these is still in flight, so neither it nor any sibling under its correlation id may be purged.</summary>
    private static readonly DownloadJobStatus[] NonTerminalJobStatuses =
    [
        DownloadJobStatus.Queued,
        DownloadJobStatus.Running,
        DownloadJobStatus.Stopping,
        DownloadJobStatus.Compensating
    ];

    /// <summary>Terminal and not restartable: nothing is lost by deleting these.</summary>
    private static readonly DownloadJobStatus[] SucceededJobStatuses =
    [
        DownloadJobStatus.Completed,
        DownloadJobStatus.CompletedWithWarnings,
        DownloadJobStatus.AlreadyDownloaded,
        DownloadJobStatus.Ignored
    ];

    /// <summary>Terminal but restartable via Start; only purged when the caller opts in.</summary>
    private static readonly DownloadJobStatus[] RestartableJobStatuses =
    [
        DownloadJobStatus.Failed,
        DownloadJobStatus.Stopped
    ];

    private static readonly DownloadGroupStatus[] SucceededGroupStatuses =
    [
        DownloadGroupStatus.Completed,
        DownloadGroupStatus.CompletedWithWarnings
    ];

    private static readonly DownloadGroupStatus[] FailedGroupStatuses =
    [
        DownloadGroupStatus.CompletedWithFailures,
        DownloadGroupStatus.Failed,
        DownloadGroupStatus.Stopped
    ];

    public async Task<DownloadHistoryCleanupResult> PurgeAsync(
        int retentionDays,
        bool includeFailed,
        Func<string, Task>? reportProgress,
        CancellationToken cancellationToken)
    {
        var days = Math.Max(0, retentionDays);
        var cutoff = clock.GetCurrentInstant().Minus(Duration.FromDays(days));

        // A group is all-or-nothing: its status gates every child. That is what keeps a
        // partially-failed playlist intact in the default mode — the successful children stay so the
        // group's aggregate counts remain coherent and the failed sibling stays restartable.
        var jobStatuses = includeFailed
            ? [.. SucceededJobStatuses, .. RestartableJobStatuses]
            : SucceededJobStatuses;
        var groupStatuses = includeFailed
            ? [.. SucceededGroupStatuses, .. FailedGroupStatuses]
            : SucceededGroupStatuses;

        // Any sibling in a blocking status vetoes the whole correlation id. In the default mode
        // restartable statuses block too, so a stale RefreshGroupAggregateAsync count can never let a
        // retryable job's siblings be purged out from under it.
        var blockingStatuses = includeFailed
            ? NonTerminalJobStatuses
            : [.. NonTerminalJobStatuses, .. RestartableJobStatuses];

        if (reportProgress is not null)
        {
            var scope = includeFailed ? "finished, failed and stopped" : "successfully finished";
            await reportProgress(
                $"Purging {scope} download jobs whose group finished before {cutoff} ({days}-day retention)…");
        }

        var purgedJobs = 0;
        var purgedGroups = 0;
        var deletedJobFlows = 0;
        var deletedGroupFlows = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await SelectEligibleJobsAsync(
                cutoff,
                DownloadJobStateSql.ToPostgresLabels(jobStatuses),
                DownloadJobStateSql.ToPostgresLabels(groupStatuses),
                DownloadJobStateSql.ToPostgresLabels(blockingStatuses),
                cancellationToken);

            if (batch.Count == 0)
                break;

            var jobIds = batch.Select(x => x.JobId).ToArray();
            var correlationIds = batch.Select(x => x.CorrelationId).Distinct().ToArray();

            // Flows first: once the run rows are gone the instance ids are unrecoverable.
            deletedJobFlows += await DeleteJobFlowsAsync(jobIds, cancellationToken);

            purgedJobs += await DeleteJobRowsAsync(jobIds, cancellationToken);

            // A group only drains once its last child is gone, which for a large playlist happens on a
            // later batch than the one that started it.
            var drained = await SelectDrainedGroupsAsync(correlationIds, cancellationToken);
            if (drained.Count > 0)
            {
                deletedGroupFlows += await DeleteGroupFlowsAsync(drained.Select(x => x.GroupId), cancellationToken);
                purgedGroups += await DeleteGroupRowsAsync(drained.Select(x => x.CorrelationId).ToArray(), cancellationToken);
            }

            if (reportProgress is not null)
                await reportProgress($"Purged {purgedJobs} job(s) and {purgedGroups} group(s) so far…");

            // A short batch means the eligible set is exhausted; avoid one extra empty round-trip.
            if (batch.Count < BatchSize)
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (reportProgress is not null)
            await reportProgress("Sweeping orphaned Cleipnir flow instances…");

        var sweep = await SweepOrphanedFlowsAsync(cancellationToken);

        var result = new DownloadHistoryCleanupResult
        {
            RetentionDays = days,
            IncludeFailed = includeFailed,
            PurgedJobs = purgedJobs,
            PurgedGroups = purgedGroups,
            DeletedJobFlows = deletedJobFlows,
            DeletedGroupFlows = deletedGroupFlows,
            DeletedOrphanFlows = sweep.Deleted,
            OrphanSweepTruncated = sweep.Truncated
        };

        logger.LogInformation(
            "Download history cleanup purged {Jobs} job(s), {Groups} group(s), {JobFlows} job flow(s), "
            + "{GroupFlows} group flow(s) and {Orphans} orphaned flow(s) using a {Days}-day retention "
            + "(includeFailed={IncludeFailed}).",
            purgedJobs, purgedGroups, deletedJobFlows, deletedGroupFlows, sweep.Deleted, days, includeFailed);

        return result;
    }

    private async Task<List<(Guid JobId, Guid CorrelationId)>> SelectEligibleJobsAsync(
        Instant cutoff,
        string[] jobStatuses,
        string[] groupStatuses,
        string[] blockingStatuses,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT dj.job_id, dj.correlation_id
            FROM jobs.download_jobs dj
            JOIN jobs.download_groups g ON g.correlation_id = dj.correlation_id
            WHERE dj.status::text = ANY(@job_statuses)
              AND g.status::text = ANY(@group_statuses)
              AND COALESCE(dj.completed_at, dj.updated_at) < @cutoff
              AND COALESCE(g.completed_at, g.updated_at) < @cutoff
              AND NOT EXISTS (
                  SELECT 1
                  FROM jobs.download_jobs sibling
                  WHERE sibling.correlation_id = dj.correlation_id
                    AND sibling.status::text = ANY(@blocking_statuses)
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM jobs.download_job_runs r
                  JOIN jobs.download_worker_leases wl ON wl.run_id = r.run_id
                  WHERE r.job_id = dj.job_id
                    AND wl.status::text = 'active'
              )
            ORDER BY dj.job_id
            LIMIT @batch_size;
            """);

        AddTextArray(command, "job_statuses", jobStatuses);
        AddTextArray(command, "group_statuses", groupStatuses);
        AddTextArray(command, "blocking_statuses", blockingStatuses);
        command.Parameters.AddWithValue("cutoff", cutoff.ToDateTimeOffset());
        command.Parameters.AddWithValue("batch_size", BatchSize);
        command.CommandTimeout = 0;

        var rows = new List<(Guid, Guid)>(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));

        return rows;
    }

    private async Task<int> DeleteJobRowsAsync(Guid[] jobIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Deleting the job row cascades to download_job_runs (and through it to stage_attempts,
        // artifacts, worker_leases and warnings), download_job_history, download_job_progress_log and
        // playlist_items, and nulls media.media_source_versions.latest_job_id.
        var deleted = await ExecuteAsync(
            connection, transaction, "DELETE FROM jobs.download_jobs WHERE job_id = ANY(@ids);", jobIds, cancellationToken);

        // Neither of these has a foreign key to download_jobs, deliberately, so nothing cascades.
        await ExecuteAsync(
            connection, transaction, "DELETE FROM jobs.failed_download_jobs WHERE job_id = ANY(@ids);", jobIds, cancellationToken);
        await ExecuteAsync(
            connection, transaction, "DELETE FROM jobs.processed_messages WHERE job_id = ANY(@ids);", jobIds, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<List<(Guid GroupId, Guid CorrelationId)>> SelectDrainedGroupsAsync(
        Guid[] correlationIds,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT g.group_id, g.correlation_id
            FROM jobs.download_groups g
            WHERE g.correlation_id = ANY(@correlation_ids)
              AND NOT EXISTS (
                  SELECT 1
                  FROM jobs.download_jobs dj
                  WHERE dj.correlation_id = g.correlation_id
              );
            """);
        command.Parameters.AddWithValue("correlation_ids", correlationIds);
        command.CommandTimeout = 0;

        var rows = new List<(Guid, Guid)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));

        return rows;
    }

    private async Task<int> DeleteGroupRowsAsync(Guid[] correlationIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Post-fan-out staging debris. jobs.playlists itself is deliberately left alone: both
        // playlists.media_playlist_membership and playlists.playlist_metadata cascade off it, and those
        // hold which media belong to which playlist — user library data, not job history.
        await ExecuteAsync(connection, transaction, """
            DELETE FROM jobs.playlist_scan_entries
            WHERE playlist_id IN (
                SELECT p.playlist_id FROM jobs.playlists p WHERE p.correlation_id = ANY(@ids)
            );
            """, correlationIds, cancellationToken);

        // No foreign key ties groups to jobs, so the group row must be removed explicitly. Re-check
        // for children inside the transaction in case one was inserted since the drain check.
        var deleted = await ExecuteAsync(connection, transaction, """
            DELETE FROM jobs.download_groups g
            WHERE g.correlation_id = ANY(@ids)
              AND NOT EXISTS (
                  SELECT 1 FROM jobs.download_jobs dj WHERE dj.correlation_id = g.correlation_id
              );
            """, correlationIds, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<int> DeleteJobFlowsAsync(Guid[] jobIds, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT job_id, run_id FROM jobs.download_job_runs WHERE job_id = ANY(@job_ids);");
        command.Parameters.AddWithValue("job_ids", jobIds);
        command.CommandTimeout = 0;

        var runs = new List<(Guid JobId, Guid RunId)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                runs.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        var deleted = 0;
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DeleteJobFlowAsync(run.JobId, run.RunId))
                deleted++;
        }

        return deleted;
    }

    private async Task<int> DeleteGroupFlowsAsync(IEnumerable<Guid> groupIds, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var groupId in groupIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DeleteGroupFlowAsync(groupId))
                deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// Removes terminal flow instances whose owning row is already gone. These accumulate from two
    /// sources: rows deleted before this cleanup existed, and local-import flows, which nothing has
    /// ever deleted. Deletion goes through Cleipnir's own control panel rather than raw SQL so
    /// <c>flows</c>, <c>flows_effects</c>, <c>flows_messages</c> and <c>flows_timeouts</c> stay consistent.
    /// </summary>
    private async Task<(int Deleted, bool Truncated)> SweepOrphanedFlowsAsync(CancellationToken cancellationToken)
    {
        // Orphan detection happens entirely in SQL so only genuine orphans cross the wire. Doing the
        // shape test in C# instead would mean streaming every terminal flow on the install — the
        // overwhelming majority of which have a live owner — and would make the LIMIT below sample a
        // nondeterministic subset each run rather than meaning "there are more orphans left".
        //
        // The instance-id shapes come from DownloadFlowInstance.Job/.Group and
        // LocalImportFlowInstance.ForItemAttempt. PostgreSQL accepts undashed 32-hex as a uuid literal,
        // so substr(...)::uuid resolves each id back to its owning table.
        //
        // Only settled flows are candidates: Postponed/Suspended instances are still live. The status
        // ints are read off Cleipnir's own enum rather than hardcoded.
        var terminalStatuses = new[] { (int)Status.Succeeded, (int)Status.Failed };

        await using var command = dataSource.CreateCommand("""
            SELECT f.human_instance_id
            FROM cleipnir.flows f
            WHERE f.status = ANY(@statuses)
              AND f.human_instance_id IS NOT NULL
              AND (
                  (f.human_instance_id ~ '^[0-9a-fA-F]{32}-[0-9a-fA-F]{32}$'
                   AND NOT EXISTS (
                       SELECT 1 FROM jobs.download_job_runs r
                       WHERE r.job_id = substr(f.human_instance_id, 1, 32)::uuid
                         AND r.run_id = substr(f.human_instance_id, 34, 32)::uuid))
                  OR (f.human_instance_id ~ '^[0-9a-fA-F]{32}$'
                   AND NOT EXISTS (
                       SELECT 1 FROM jobs.download_groups g
                       WHERE g.group_id = f.human_instance_id::uuid))
                  OR (f.human_instance_id ~ '^[0-9a-fA-F]{32}/attempt-[0-9]+$'
                   AND NOT EXISTS (
                       SELECT 1 FROM imports.import_session_items i
                       WHERE i.item_id = substr(f.human_instance_id, 1, 32)::uuid))
              )
            ORDER BY f.human_instance_id
            LIMIT @limit;
            """);
        command.Parameters.AddWithValue("statuses", terminalStatuses);
        command.Parameters.AddWithValue("limit", OrphanSweepLimit);
        command.CommandTimeout = 0;

        var orphans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                orphans.Add(reader.GetString(0));
        }

        // Deletion still goes through the typed container matching each id shape, so Cleipnir keeps
        // flows/flows_effects/flows_messages/flows_timeouts consistent. A panel fetched from the wrong
        // container would simply come back null, because the (type, instance) key would not match.
        var deleted = 0;
        foreach (var instance in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var removed = TryParseJobInstance(instance, out var jobId, out var runId)
                ? await DeleteJobFlowAsync(jobId, runId)
                : TryParseGroupInstance(instance, out var groupId)
                    ? await DeleteGroupFlowAsync(groupId)
                    : await DeleteImportFlowAsync(instance);

            if (removed)
                deleted++;
        }

        return (deleted, orphans.Count >= OrphanSweepLimit);
    }

    // One method per flow container: each container's ControlPanel returns a differently-typed panel,
    // so `var` at three call sites is simpler than a generic helper. A missing panel is not an error —
    // it just means that flow was already deleted, which is the normal steady state.
    private async Task<bool> DeleteJobFlowAsync(Guid jobId, Guid runId)
    {
        var instance = DownloadFlowInstance.Job(jobId, runId);
        try
        {
            var panel = await jobFlows.ControlPanel(new FlowInstance(instance));
            if (panel is null)
                return false;
            await panel.Delete();
            return true;
        }
        catch (Exception ex)
        {
            LogFlowDeleteFailure(ex, instance);
            return false;
        }
    }

    private async Task<bool> DeleteGroupFlowAsync(Guid groupId)
    {
        var instance = DownloadFlowInstance.Group(groupId);
        try
        {
            var panel = await groupFlows.ControlPanel(new FlowInstance(instance));
            if (panel is null)
                return false;
            await panel.Delete();
            return true;
        }
        catch (Exception ex)
        {
            LogFlowDeleteFailure(ex, instance);
            return false;
        }
    }

    private async Task<bool> DeleteImportFlowAsync(string instance)
    {
        try
        {
            var panel = await importFlows.ControlPanel(new FlowInstance(instance));
            if (panel is null)
                return false;
            await panel.Delete();
            return true;
        }
        catch (Exception ex)
        {
            LogFlowDeleteFailure(ex, instance);
            return false;
        }
    }

    // A single stubborn flow instance must not abort the whole purge: the SQL rows it names are still
    // present, so skipping it is safe and the next run retries.
    private void LogFlowDeleteFailure(Exception ex, string instance)
        => logger.LogWarning(ex, "Failed deleting Cleipnir flow instance {Instance} during download history cleanup; skipping it.", instance);

    private static void AddTextArray(NpgsqlCommand command, string name, string[] values)
        => command.Parameters.Add(name, NpgsqlDbType.Array | NpgsqlDbType.Text).Value = values;

    /// <summary>Runs a delete whose only parameter is the <c>@ids</c> uuid array.</summary>
    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid[] ids,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ids", ids);
        command.CommandTimeout = 0;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Job flow instances are <c>{jobId:N}-{runId:N}</c> — see <see cref="DownloadFlowInstance.Job"/>.</summary>
    private static bool TryParseJobInstance(string instance, out Guid jobId, out Guid runId)
    {
        jobId = default;
        runId = default;
        return instance.Length == 65
               && instance[32] == '-'
               && Guid.TryParseExact(instance[..32], "N", out jobId)
               && Guid.TryParseExact(instance[33..], "N", out runId);
    }

    /// <summary>Group flow instances are <c>{groupId:N}</c> — see <see cref="DownloadFlowInstance.Group"/>.</summary>
    private static bool TryParseGroupInstance(string instance, out Guid groupId)
    {
        groupId = default;
        return instance.Length == 32 && Guid.TryParseExact(instance, "N", out groupId);
    }

    /// <summary>Local-import instances are <c>{itemId:N}/attempt-{n}</c> — see <c>LocalImportFlowInstance.ForItemAttempt</c>.</summary>
    private static bool TryParseImportInstance(string instance, out Guid itemId)
    {
        itemId = default;
        var separator = instance.IndexOf("/attempt-", StringComparison.Ordinal);
        return separator == 32 && Guid.TryParseExact(instance[..32], "N", out itemId);
    }
}
