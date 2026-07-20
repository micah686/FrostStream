using Conduit.NATS;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Data;

/// <summary>
/// Publishes a live, non-persistent notification when a download job's status or stage changes so
/// WebAPI's queue hub can push it over SSE. PostgreSQL is authoritative; this is best-effort
/// telemetry, so a publish failure must never fail the DB write that triggered it.
/// </summary>
public interface IDownloadJobStateNotifier
{
    Task NotifyAsync(Guid jobId, DownloadJobState newState, DownloadJobState previousState, Guid correlationId, CancellationToken ct = default);

    Task NotifyV2Async(
        Guid jobId,
        Guid correlationId,
        DownloadJobStatus status,
        DownloadJobStatus previousStatus,
        DownloadStage stage,
        DownloadStageStatus stageStatus,
        Guid? runId,
        int runNumber,
        int attempt,
        string? artifactKey,
        int warningCount,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Broadcasts <see cref="DownloadQueueStateChanged"/> over core NATS. Swallows/logs failures.</summary>
public sealed class DownloadJobStateNotifier(
    IMessageBus messageBus,
    IClock clock,
    ILogger<DownloadJobStateNotifier> logger) : IDownloadJobStateNotifier
{
    public async Task NotifyAsync(
        Guid jobId,
        DownloadJobState newState,
        DownloadJobState previousState,
        Guid correlationId,
        CancellationToken ct = default)
    {
        try
        {
            await messageBus.PublishAsync(
                DownloadQueueSubjects.StateChanged,
                new DownloadQueueStateChanged
                {
                    JobId = jobId,
                    State = newState,
                    PreviousState = previousState,
                    CorrelationId = correlationId,
                    OccurredAt = clock.GetCurrentInstant()
                },
                ct);
        }
        catch (Exception ex)
        {
            // Non-persistent notification: never let a publish hiccup break the state write.
            logger.LogWarning(ex, "Failed publishing state-changed notification for JobId {JobId} ({Previous} -> {State}).",
                jobId, previousState, newState);
        }
    }

    public async Task NotifyV2Async(
        Guid jobId,
        Guid correlationId,
        DownloadJobStatus status,
        DownloadJobStatus previousStatus,
        DownloadStage stage,
        DownloadStageStatus stageStatus,
        Guid? runId,
        int runNumber,
        int attempt,
        string? artifactKey,
        int warningCount,
        CancellationToken ct = default)
    {
        try
        {
            await messageBus.PublishAsync(
                DownloadQueueSubjects.StateChanged,
                new DownloadQueueStateChanged
                {
                    JobId = jobId,
                    CorrelationId = correlationId,
                    Status = status,
                    PreviousStatus = previousStatus,
                    Stage = stage,
                    StageStatus = stageStatus,
                    RunId = runId,
                    RunNumber = runNumber,
                    Attempt = attempt,
                    ArtifactKey = artifactKey,
                    WarningCount = warningCount,
                    OccurredAt = clock.GetCurrentInstant()
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed publishing V2 state notification for JobId {JobId} ({Previous} -> {Status}, {Stage}/{StageStatus}).",
                jobId, previousStatus, status, stage, stageStatus);
        }
    }
}

/// <summary>No-op notifier for tests and contexts without a message bus.</summary>
public sealed class NullDownloadJobStateNotifier : IDownloadJobStateNotifier
{
    public static readonly NullDownloadJobStateNotifier Instance = new();

    public Task NotifyAsync(Guid jobId, DownloadJobState newState, DownloadJobState previousState, Guid correlationId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task NotifyV2Async(Guid jobId, Guid correlationId, DownloadJobStatus status, DownloadJobStatus previousStatus,
        DownloadStage stage, DownloadStageStatus stageStatus, Guid? runId, int runNumber, int attempt, string? artifactKey,
        int warningCount, CancellationToken ct = default) => Task.CompletedTask;
}
