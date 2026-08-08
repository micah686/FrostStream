using Cleipnir.ResilientFunctions.Domain;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class DownloadFlowStartupState(IClock clock)
{
    public Instant GenerationStartedAt { get; private set; } = Instant.MaxValue;
    internal void MarkReady() => GenerationStartedAt = clock.GetCurrentInstant();
}

/// <summary>
/// A blocking startup gate. It deletes non-terminal download flow instances, reconciles
/// PostgreSQL, and completes before any V2 ingress/worker-result consumer is started.
/// </summary>
public sealed class DownloadFlowStartupService(
    IServiceScopeFactory scopeFactory,
    DownloadJobV2Flows v2Flows,
    DownloadGroupV2Flows groupFlows,
    DownloadFlowStartupState state,
    ILogger<DownloadFlowStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
        foreach (var run in knownRuns)
        {
            var panel = await v2Flows.ControlPanel(new FlowInstance(DownloadFlowInstance.Job(run.JobId, run.RunId)));
            if (panel is not null)
                await panel.Delete();
        }

        var knownGroupIds = await db.DownloadGroups.AsNoTracking()
            .Select(x => x.GroupId)
            .ToListAsync(cancellationToken);
        foreach (var groupId in knownGroupIds)
        {
            var panel = await groupFlows.ControlPanel(new FlowInstance(DownloadFlowInstance.Group(groupId)));
            if (panel is not null)
                await panel.Delete();
        }

        var result = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
            .ReconcileForStartupAsync(cancellationToken);
        state.MarkReady();
        logger.LogInformation(
            "Download V2 startup reconciliation complete: {RunFlows} non-terminal run flows and {GroupFlows} group flows deleted; {Queued} queued jobs stopped, {Active} active jobs failed, {Groups} active groups failed, {Leases} leases expired.",
            knownRuns.Count, knownGroupIds.Count, result.StoppedQueuedJobs, result.FailedActiveJobs,
            result.FailedActiveGroups, result.ExpiredLeases);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
