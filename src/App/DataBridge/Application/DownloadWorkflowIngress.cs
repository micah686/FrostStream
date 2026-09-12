using Cleipnir.ResilientFunctions.Domain.Exceptions;
using DataBridge.Data;
using DataBridge.Flows;
using DataBridge.Messaging;
using Shared.Application;
using Shared.Messaging;

namespace DataBridge.Application;

public interface IDownloadWorkflowStarter
{
    Task StartJobAsync(Guid jobId, DownloadRunRequest run, CancellationToken cancellationToken = default);
    Task StartGroupAsync(DownloadGroupRequested request, CancellationToken cancellationToken = default);
}

public sealed class CleipnirDownloadWorkflowStarter(
    DownloadJobV2Flows jobFlows,
    DownloadGroupV2Flows groupFlows) : IDownloadWorkflowStarter
{
    public Task StartJobAsync(Guid jobId, DownloadRunRequest run, CancellationToken cancellationToken = default)
        => jobFlows.Run(DownloadFlowInstance.Job(jobId, run.RunId), run);

    public Task StartGroupAsync(DownloadGroupRequested request, CancellationToken cancellationToken = default)
        => groupFlows.Run(DownloadFlowInstance.Group(request.GroupId), request);
}

/// <summary>
/// Commits download/group intent and deduplication state independently of JetStream. Full and Lite
/// adapters may call the same operation; neither transport acknowledgment nor an in-memory queue is
/// part of the acceptance transaction.
/// </summary>
public sealed class DownloadWorkflowIngress(
    IDownloadJobsRepository legacyRepository,
    IDownloadFlowV2Repository repository,
    IDownloadWorkflowStarter starter,
    DownloadFlowStartupState startupState) : IDownloadWorkflowIngress
{
    public async Task<DurableWorkReceipt> AcceptAsync(
        DownloadRequested request,
        CancellationToken cancellationToken = default)
    {
        if (await legacyRepository.IsMessageProcessedAsync(request.MessageId, cancellationToken))
        {
            return Receipt(DurableWorkDisposition.Duplicate, request.JobId, request.MessageId);
        }

        // Requests from an earlier process generation remain visible but paused. Restarting a host
        // must never silently resume external work merely because a durable message is still pending.
        var autoStart = request.OccurredAt >= startupState.GenerationStartedAt;
        var run = await repository.CreateInitialRunAsync(request, autoStart, cancellationToken);
        if (run is not null)
        {
            try
            {
                await starter.StartJobAsync(request.JobId, run, cancellationToken);
            }
            catch (InvocationSuspendedException)
            {
                // Cleipnir durably reached its expected wait-for-executor checkpoint.
            }
        }

        await legacyRepository.MarkMessageProcessedAsync(
            request.MessageId,
            request.OperationKey,
            request.JobId,
            cancellationToken);

        return Receipt(
            autoStart ? DurableWorkDisposition.Accepted : DurableWorkDisposition.PersistedButPaused,
            request.JobId,
            request.MessageId);
    }

    public async Task<DurableWorkReceipt> AcceptAsync(
        DownloadGroupRequested request,
        CancellationToken cancellationToken = default)
    {
        await repository.CreateGroupIfMissingAsync(request, cancellationToken);

        if (request.OccurredAt < startupState.GenerationStartedAt)
        {
            if (request.Kind == DownloadGroupKind.Direct && request.DirectRequest is { } direct)
                await repository.CreateInitialRunAsync(direct, autoStart: false, cancellationToken);

            await repository.SetGroupStatusAsync(
                request.GroupId,
                DownloadGroupStatus.Stopped,
                ct: cancellationToken);
            return Receipt(DurableWorkDisposition.PersistedButPaused, request.GroupId, request.MessageId);
        }

        try
        {
            await starter.StartGroupAsync(request, cancellationToken);
        }
        catch (InvocationSuspendedException)
        {
            // Cleipnir durably reached its expected wait-for-executor checkpoint.
        }

        return Receipt(DurableWorkDisposition.Accepted, request.GroupId, request.MessageId);
    }

    private static DurableWorkReceipt Receipt(
        DurableWorkDisposition disposition,
        Guid workId,
        Guid messageId)
        => new(disposition, workId, messageId.ToString("N"));
}
