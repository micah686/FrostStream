using Conduit.NATS;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Data;

/// <summary>
/// Publishes a live, non-persistent notification when a download job's state changes, so WebAPI's
/// queue hub can push it over SSE. The authoritative record is always the <c>state</c> column —
/// this is best-effort telemetry, so a publish failure must never fail the DB write that triggered it.
/// </summary>
public interface IDownloadJobStateNotifier
{
    Task NotifyAsync(Guid jobId, DownloadJobState newState, DownloadJobState previousState, Guid correlationId, CancellationToken ct = default);
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
}

/// <summary>No-op notifier for tests and contexts without a message bus.</summary>
public sealed class NullDownloadJobStateNotifier : IDownloadJobStateNotifier
{
    public static readonly NullDownloadJobStateNotifier Instance = new();

    public Task NotifyAsync(Guid jobId, DownloadJobState newState, DownloadJobState previousState, Guid correlationId, CancellationToken ct = default)
        => Task.CompletedTask;
}
