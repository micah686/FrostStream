using DataBridge.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Cleipnir.ResilientFunctions.Domain;
using DataBridge.Flows;
using DataBridge.Messaging;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
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
    IServiceScopeFactory scopes,
    LocalImportItemV2Flows importFlows,
    IClock clock,
    ILogger<ImportSessionPurger> logger) : IImportSessionPurger
{
    private async Task<T> ApplicationAsync<T>(Func<ApplicationRetention, Task<T>> operation)
    {
        using var scope = scopes.CreateScope();
        return await operation(new ApplicationRetention(scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>()));
    }

    public const int DefaultRetentionDays = 30;

    /// <summary>Sessions are purged a batch at a time so a first run on a long-lived install never holds one huge transaction.</summary>
    private const int BatchSize = ApplicationBatches.WriteBatchSize;

    public async Task<ImportSessionCleanupResult> PurgeAsync(
        int retentionDays,
        Func<string, Task>? reportProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            purgedSessions += await DeleteSessionRowsAsync(sessionIdArray, cutoff, cancellationToken);

            if (reportProgress is not null)
                await reportProgress($"Purged {purgedSessions} import session(s) and {deletedFlows} flow instance(s) so far…");

            // A short batch means the eligible set is exhausted; avoid one extra empty round-trip.
            if (sessionIds.Count < BatchSize)
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();
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

    private Task<List<Guid>> SelectEligibleSessionIdsAsync(Instant cutoff, CancellationToken ct)
        => ApplicationAsync(store => store.SelectSessionsAsync(cutoff, ct));

    private Task<List<Guid>> SelectSessionItemIdsAsync(Guid[] ids, CancellationToken ct)
        => ApplicationAsync(store => store.SelectSessionItemsAsync(ids, ct));

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
        command.CommandTimeout = 15;

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
            if (panel is null || panel.Status is not (Status.Succeeded or Status.Failed))
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

    private Task<int> DeleteSessionRowsAsync(Guid[] ids, Instant cutoff, CancellationToken ct)
        => ApplicationAsync(store => store.DeleteSessionsAsync(ids, cutoff, ct));

}
