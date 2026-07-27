using Cleipnir.ResilientFunctions.Domain;
using DataBridge.Flows;
using DataBridge.Messaging;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using NpgsqlTypes;
using Shared.Messaging;

namespace DataBridge.Data;

/// <summary>
/// Deletes terminal local-media import sessions and the durable <see cref="LocalImportItemFlow"/>
/// instances that drove them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flows are deleted before the rows that name them.</b> There is no referential relationship
/// between the <c>imports</c> schema and the <c>cleipnir</c> schema — the only link is the
/// instance-id string convention in <see cref="LocalImportFlowInstance"/>, stored in
/// <c>cleipnir.flows.human_instance_id</c>. Deleting the session/item row first would leave a flow
/// nobody can name, and therefore nobody can ever delete. If the process dies between the flow
/// delete and the row delete the rows simply survive and the next run retries, so this ordering is
/// also the crash-safe one.
/// </para>
/// </remarks>
public sealed class ImportSessionPurger(
    NpgsqlDataSource dataSource,
    LocalImportItemV2Flows importFlows,
    IClock clock,
    ILogger<ImportSessionPurger> logger) : IImportSessionPurger
{
    public const int DefaultRetentionDays = 30;

    /// <summary>Sessions are purged a batch at a time so a first run on a long-lived install never holds one huge transaction.</summary>
    private const int BatchSize = 500;

    /// <summary>Terminal statuses for an import session. Once a session reaches one of these it will never return to a non-terminal state.</summary>
    private static readonly ImportSessionStatus[] TerminalSessionStatuses =
    [
        ImportSessionStatus.ScanFailed,
        ImportSessionStatus.Completed,
        ImportSessionStatus.CompletedWithFailures,
        ImportSessionStatus.Cancelled
    ];

    public async Task<ImportSessionCleanupResult> PurgeAsync(
        int retentionDays,
        Func<string, Task>? reportProgress,
        CancellationToken cancellationToken)
    {
        var days = Math.Max(0, retentionDays);
        var cutoff = clock.GetCurrentInstant().Minus(Duration.FromDays(days));

        if (reportProgress is not null)
            await reportProgress($"Purging terminal import sessions completed before {cutoff} ({days}-day retention)…");

        var purgedSessions = 0;
        var deletedFlows = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var sessionIds = await SelectEligibleSessionIdsAsync(cutoff, cancellationToken);
            if (sessionIds.Count == 0)
                break;

            var sessionIdArray = sessionIds.ToArray();
            var itemIds = await SelectSessionItemIdsAsync(sessionIdArray, cancellationToken);

            // Flows first: once the item rows are gone the instance ids are unrecoverable.
            deletedFlows += await DeleteImportFlowsAsync(itemIds, cancellationToken);
            purgedSessions += await DeleteSessionRowsAsync(sessionIdArray, cancellationToken);

            if (reportProgress is not null)
                await reportProgress($"Purged {purgedSessions} import session(s) and {deletedFlows} flow instance(s) so far…");

            // A short batch means the eligible set is exhausted; avoid one extra empty round-trip.
            if (sessionIds.Count < BatchSize)
                break;
        }

        logger.LogInformation(
            "Import session cleanup purged {Sessions} session(s) and {Flows} local-import flow instance(s) using a {Days}-day retention.",
            purgedSessions, deletedFlows, days);

        return new ImportSessionCleanupResult
        {
            RetentionDays = days,
            PurgedSessions = purgedSessions,
            DeletedFlows = deletedFlows
        };
    }

    private async Task<List<Guid>> SelectEligibleSessionIdsAsync(
        Instant cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT session_id
            FROM imports.import_sessions
            WHERE status = ANY(@statuses)
              AND completed_at < @cutoff
            ORDER BY session_id
            LIMIT @batch_size;
            """);

        AddTextArray(command, "statuses", DownloadJobStateSql.ToPostgresLabels(TerminalSessionStatuses));
        command.Parameters.AddWithValue("cutoff", cutoff.ToDateTimeOffset());
        command.Parameters.AddWithValue("batch_size", BatchSize);
        command.CommandTimeout = 0;

        var sessionIds = new List<Guid>(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessionIds.Add(reader.GetGuid(0));

        return sessionIds;
    }

    private async Task<List<Guid>> SelectSessionItemIdsAsync(
        Guid[] sessionIds,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT item_id
            FROM imports.import_session_items
            WHERE session_id = ANY(@session_ids);
            """);
        command.Parameters.AddWithValue("session_ids", sessionIds);
        command.CommandTimeout = 0;

        var itemIds = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            itemIds.Add(reader.GetGuid(0));

        return itemIds;
    }

    private async Task<int> DeleteImportFlowsAsync(
        List<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
            return 0;

        var terminalStatuses = new[] { (int)Status.Succeeded, (int)Status.Failed };

        await using var command = dataSource.CreateCommand("""
            SELECT human_instance_id
            FROM cleipnir.flows
            WHERE status = ANY(@statuses)
              AND human_instance_id ~ '^[0-9a-fA-F]{32}/attempt-[0-9]+$'
              AND substr(human_instance_id, 1, 32)::uuid = ANY(@item_ids)
            ORDER BY human_instance_id;
            """);
        command.Parameters.AddWithValue("statuses", terminalStatuses);
        command.Parameters.AddWithValue("item_ids", itemIds.ToArray());
        command.CommandTimeout = 0;

        var instanceIds = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                instanceIds.Add(reader.GetString(0));
        }

        var deleted = 0;
        foreach (var instance in instanceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DeleteImportFlowAsync(instance))
                deleted++;
        }

        return deleted;
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
            logger.LogWarning(ex, "Failed deleting local-import Cleipnir flow instance {Instance} during import session cleanup; skipping it.", instance);
            return false;
        }
    }

    private async Task<int> DeleteSessionRowsAsync(
        Guid[] sessionIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Cascade deletes import_session_items and import_session_mappings.
        await using var command = new NpgsqlCommand(
            "DELETE FROM imports.import_sessions WHERE session_id = ANY(@ids);",
            connection,
            transaction);
        command.Parameters.AddWithValue("ids", sessionIds);
        command.CommandTimeout = 0;

        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private static void AddTextArray(NpgsqlCommand command, string name, string[] values)
        => command.Parameters.Add(name, NpgsqlDbType.Array | NpgsqlDbType.Text).Value = values;
}
