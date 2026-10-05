using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class DownloadFlowStartupState
{
    public Instant GenerationStartedAt { get; private set; } = Instant.MaxValue;
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    internal void BeginStartup() { Volatile.Write(ref ready, 0); GenerationStartedAt = Instant.MaxValue; }
    internal void MarkReady(Instant generation) { GenerationStartedAt = generation; Volatile.Write(ref ready, 1); }
}

/// <summary>
/// A blocking startup gate. It deletes non-terminal download flow instances, reconciles
/// the selected application database, and completes before any V2 ingress/worker-result consumer is started.
/// </summary>
public sealed class DownloadFlowStartupService(
    IServiceScopeFactory scopeFactory,
    IFunctionStore store,
    DownloadJobV2Flows v2Flows,
    DownloadGroupV2Flows groupFlows,
    DownloadFlowStartupState state,
    ILogger<DownloadFlowStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        state.BeginStartup();
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        // Delete flow instances for runs that are still in flight or could resume. Terminal runs
        // are left for DownloadHistoryPurger; deleting them here would make startup cost grow with
        // the size of history instead of the amount of unfinished work.
        var nonTerminalRunStatuses = new[]
        {
            DownloadJobStatus.Queued,
            DownloadJobStatus.Running,
            DownloadJobStatus.Stopping,
            DownloadJobStatus.Compensating
        };
        var knownRuns = await db.DownloadJobRuns.AsNoTracking()
            .Where(r => nonTerminalRunStatuses.Contains(r.Status))
            .Select(x => new { x.JobId, x.RunId })
            .ToListAsync(cancellationToken);
        var runInstances = knownRuns.Select(r => DownloadFlowInstance.Job(r.JobId,r.RunId)).ToHashSet(StringComparer.Ordinal);
        foreach (var instance in await UnfinishedInstancesAsync(nameof(DownloadJobV2Flow), cancellationToken)) runInstances.Add(instance);
        foreach (var instance in runInstances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var panel = await v2Flows.ControlPanel(new FlowInstance(instance));
            if (panel is not null)
                await panel.Delete();
        }

        var knownGroupIds = await db.DownloadGroups.AsNoTracking()
            .Select(x => x.GroupId)
            .ToListAsync(cancellationToken);
        var groupInstances = knownGroupIds.Select(DownloadFlowInstance.Group).ToHashSet(StringComparer.Ordinal);
        foreach (var instance in await UnfinishedInstancesAsync(nameof(DownloadGroupV2Flow), cancellationToken)) groupInstances.Add(instance);
        foreach (var instance in groupInstances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var panel = await groupFlows.ControlPanel(new FlowInstance(instance));
            if (panel is not null)
                await panel.Delete();
        }

        var result = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
            .ReconcileForStartupAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        state.MarkReady(result.GenerationStartedAt);
        logger.LogInformation(
            "Download V2 startup reconciliation complete: {RunFlows} non-terminal run flows and {GroupFlows} group flows deleted; {Queued} queued jobs stopped, {Active} active jobs failed, {Groups} active groups failed, {Leases} leases expired.",
            runInstances.Count, groupInstances.Count, result.StoppedQueuedJobs, result.FailedActiveJobs,
            result.FailedActiveGroups, result.ExpiredLeases);
    }

    private async Task<List<string>> UnfinishedInstancesAsync(string flowName, CancellationToken ct)
    {
        var types = await store.TypeStore.GetAllFlowTypes();
        if (!types.TryGetValue(new FlowType(flowName),out var type)) return [];
        var instances = new List<string>();
        foreach (var status in new[] { Status.Executing,Status.Postponed,Status.Suspended })
            foreach (var instance in await store.GetInstances(type,status))
            {
                ct.ThrowIfCancellationRequested();
                var flow = await store.GetFunction(new StoredId(type,instance));
                if (flow is not null) instances.Add(flow.HumanInstanceId);
            }
        return instances;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
