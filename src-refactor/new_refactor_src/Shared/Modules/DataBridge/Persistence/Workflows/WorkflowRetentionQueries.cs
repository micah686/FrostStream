using System.Data.Common;
using Cleipnir.ResilientFunctions.Domain;
using DataBridge.Data;
using Microsoft.Data.Sqlite;

namespace DataBridge.Persistence.Workflows;

public interface IWorkflowRetentionQueries
{
    Task<List<string>> FindOrphansAsync(int limit, CancellationToken ct);
    Task<List<string>> FindImportInstancesAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct);
}

/// <summary>Provider-specific discovery only; deletion always remains with typed Cleipnir control panels.</summary>
public sealed class WorkflowRetentionQueries(ApplicationDatabase database) : IWorkflowRetentionQueries
{
    private static void Configure(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        sqlite.CreateFunction<string, string?>("fs_flow_job", id => DownloadHistoryPurger.TryParseJobInstance(id, out var job, out _) ? job.ToString("N") : null, true);
        sqlite.CreateFunction<string, string?>("fs_flow_run", id => DownloadHistoryPurger.TryParseJobInstance(id, out _, out var run) ? run.ToString("N") : null, true);
        sqlite.CreateFunction<string, string?>("fs_flow_group", id => DownloadHistoryPurger.TryParseGroupInstance(id, out var group) ? group.ToString("N") : null, true);
        sqlite.CreateFunction<string, string?>("fs_flow_item", id => DownloadHistoryPurger.TryParseImportInstance(id, out var item) ? item.ToString("N") : null, true);
    }
    public async Task<List<string>> FindOrphansAsync(int limit, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        Configure(connection);
        await using var command = ApplicationDbCommands.Create(connection, database.Provider == PersistenceProvider.Postgres ? """
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
            """ : """
            SELECT f.human_instance_id FROM cleipnir_flows f
            WHERE f.status IN (@succeeded,@failed) AND (
                (fs_flow_job(f.human_instance_id) IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM jobs_download_job_runs r WHERE r.job_id=fs_flow_job(f.human_instance_id) AND r.run_id=fs_flow_run(f.human_instance_id)))
                OR (fs_flow_group(f.human_instance_id) IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM jobs_download_groups g WHERE g.group_id=fs_flow_group(f.human_instance_id)))
                OR (fs_flow_item(f.human_instance_id) IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM imports_import_session_items i WHERE i.item_id=fs_flow_item(f.human_instance_id))))
            ORDER BY f.human_instance_id LIMIT @limit
            """);
        if (database.Provider == PersistenceProvider.Postgres) command.Parameters.AddWithValue("statuses", new[] { (int)Status.Succeeded, (int)Status.Failed });
        else { command.Parameters.AddWithValue("succeeded", (int)Status.Succeeded); command.Parameters.AddWithValue("failed", (int)Status.Failed); }
        command.Parameters.AddWithValue("limit", limit);
        return await Read(command, ct);
    }
    public async Task<List<string>> FindImportInstancesAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0) return [];
        await using var connection = await database.OpenConnectionAsync(ct);
        Configure(connection);
        var results = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var chunk in itemIds.Distinct().Chunk(ApplicationBatches.WriteBatchSize))
        {
            var names = chunk.Select((_, index) => "@item" + index).ToArray();
            var sqliteSql = "SELECT human_instance_id FROM cleipnir_flows WHERE status IN (@succeeded,@failed) AND fs_flow_item(human_instance_id) IN (" + string.Join(",", names) + ") ORDER BY human_instance_id";
            await using var command = ApplicationDbCommands.Create(connection, database.Provider == PersistenceProvider.Postgres ? """
            SELECT human_instance_id
            FROM cleipnir.flows
            WHERE status = ANY(@statuses)
              AND human_instance_id ~ '^[0-9a-fA-F]{32}/attempt-[0-9]+$'
              AND substr(human_instance_id, 1, 32)::uuid = ANY(@item_ids)
            ORDER BY human_instance_id;
            """ : sqliteSql);
            if (database.Provider == PersistenceProvider.Postgres)
            {
                command.Parameters.AddWithValue("statuses", new[] { (int)Status.Succeeded, (int)Status.Failed });
                command.Parameters.AddWithValue("item_ids", chunk);
            }
            else
            {
                command.Parameters.AddWithValue("succeeded", (int)Status.Succeeded);
                command.Parameters.AddWithValue("failed", (int)Status.Failed);
                for (var j = 0; j < chunk.Length; j++) command.Parameters.AddWithValue(names[j], chunk[j]);
            }
            foreach (var instance in await Read(command, ct)) results.Add(instance);
        }
        return results.ToList();
    }
    private static async Task<List<string>> Read(DbCommand command, CancellationToken ct)
    {
        command.CommandTimeout = 15;
        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(reader.GetString(0));
        return results;
    }
}
