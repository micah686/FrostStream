using Conduit.NATS;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Shared.Messaging;

/// <summary>
/// Publishes <see cref="BackgroundRunSubjects"/> telemetry for one scheduled/background execution.
/// Used by every service that runs scheduled work (DataBridge, Worker, BackupService) so the
/// Jobs &gt; Background surface sees a uniform shape regardless of who executes the task.
/// </summary>
public interface IBackgroundRunReporter
{
    /// <summary>
    /// Announces a run and returns its scope. Dispose the scope to publish the completion event —
    /// wrap the handler body in <c>await using</c> so an exception still closes the run out rather
    /// than leaving it pinned as "running" in the UI until the server restarts.
    /// </summary>
    Task<IBackgroundRunScope> BeginAsync(
        string taskType,
        ScheduledBackgroundRequest request,
        string? detail = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Announces a scheduled run for a component that holds the schedule's identifiers directly
    /// rather than the originating request (BackupCoordinator only keeps its own job record).
    /// </summary>
    Task<IBackgroundRunScope> BeginScheduledAsync(
        string taskType,
        string scheduleKey,
        string idempotencyKey,
        string? detail = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Announces a run that a user started directly rather than one a schedule triggered — an
    /// admin-initiated backup, for example. Such work never passes through a scheduled-request
    /// consumer, so it has no schedule key to report.
    /// </summary>
    Task<IBackgroundRunScope> BeginManualAsync(
        string taskType,
        string idempotencyKey,
        string? detail = null,
        CancellationToken cancellationToken = default);
}

public interface IBackgroundRunScope : IAsyncDisposable
{
    Guid RunId { get; }

    /// <summary>Reports a step. <paramref name="current"/>/<paramref name="total"/> drive the progress bar.</summary>
    Task ReportAsync(string message, int? current = null, int? total = null, double? percent = null);

    /// <summary>Marks the run successful. The completion event fires on dispose.</summary>
    void Succeed(string? summary = null);

    /// <summary>Marks the run failed. The completion event fires on dispose.</summary>
    void Fail(string errorMessage);
}

public sealed class BackgroundRunReporter(
    IMessageBus messageBus,
    IClock clock,
    string origin,
    ILogger<BackgroundRunReporter>? logger = null) : IBackgroundRunReporter
{
    public Task<IBackgroundRunScope> BeginAsync(
        string taskType,
        ScheduledBackgroundRequest request,
        string? detail = null,
        CancellationToken cancellationToken = default)
        // Requests raised by an API call carry a sentinel schedule key rather than a real schedule,
        // so classify by the key instead of assuming everything on this path came from the Scheduler.
        => BeginCoreAsync(
            taskType,
            request.ScheduleKey,
            BackgroundJobRequestFactory.IsManualScheduleKey(request.ScheduleKey)
                ? BackgroundRunTrigger.Manual
                : BackgroundRunTrigger.Scheduled,
            request.IdempotencyKey,
            detail,
            cancellationToken);

    public Task<IBackgroundRunScope> BeginScheduledAsync(
        string taskType,
        string scheduleKey,
        string idempotencyKey,
        string? detail = null,
        CancellationToken cancellationToken = default)
        => BeginCoreAsync(taskType, scheduleKey, BackgroundRunTrigger.Scheduled,
            idempotencyKey, detail, cancellationToken);

    public Task<IBackgroundRunScope> BeginManualAsync(
        string taskType,
        string idempotencyKey,
        string? detail = null,
        CancellationToken cancellationToken = default)
        => BeginCoreAsync(taskType, scheduleKey: null, BackgroundRunTrigger.Manual,
            idempotencyKey, detail, cancellationToken);

    private async Task<IBackgroundRunScope> BeginCoreAsync(
        string taskType,
        string? scheduleKey,
        BackgroundRunTrigger trigger,
        string idempotencyKey,
        string? detail,
        CancellationToken cancellationToken)
    {
        // A scheduled run has already been announced as queued by the Scheduler under an id derived
        // from the idempotency key; reuse it so this start updates that row instead of adding one.
        var runId = trigger == BackgroundRunTrigger.Scheduled
            ? BackgroundRunIds.ForIdempotencyKey(idempotencyKey)
            : Guid.NewGuid();
        var scope = new Scope(messageBus, clock, logger, runId);
        await Publish(BackgroundRunSubjects.Started, new BackgroundRunStarted
        {
            RunId = scope.RunId,
            TaskType = taskType,
            ScheduleKey = scheduleKey,
            Trigger = trigger,
            IdempotencyKey = idempotencyKey,
            Origin = origin,
            Detail = detail,
            StartedAt = clock.GetCurrentInstant()
        }, cancellationToken);
        return scope;
    }

    private async Task Publish<T>(string subject, T message, CancellationToken cancellationToken)
    {
        try
        {
            await messageBus.PublishAsync(subject, message, cancellationToken);
        }
        catch (Exception ex)
        {
            // Telemetry only: never let a publish hiccup fail the work it is describing.
            logger?.LogWarning(ex, "Failed publishing background run telemetry to {Subject}.", subject);
        }
    }

    private sealed class Scope(IMessageBus messageBus, IClock clock, ILogger? logger, Guid runId) : IBackgroundRunScope
    {
        private bool _completed;
        private bool _success;
        private string? _summary;
        private string? _error;

        public Guid RunId => runId;

        public async Task ReportAsync(string message, int? current = null, int? total = null, double? percent = null)
        {
            var resolved = percent ?? (total is > 0 && current is not null
                ? Math.Clamp(current.Value * 100d / total.Value, 0, 100)
                : null);

            await Publish(BackgroundRunSubjects.Progress, new BackgroundRunProgress
            {
                RunId = runId,
                Message = message,
                Current = current,
                Total = total,
                Percent = resolved,
                OccurredAt = clock.GetCurrentInstant()
            });
        }

        public void Succeed(string? summary = null)
        {
            _success = true;
            _summary = summary;
            _error = null;
        }

        public void Fail(string errorMessage)
        {
            _success = false;
            _error = errorMessage;
        }

        public async ValueTask DisposeAsync()
        {
            if (_completed)
                return;
            _completed = true;

            await Publish(BackgroundRunSubjects.Completed, new BackgroundRunCompleted
            {
                RunId = runId,
                Success = _success,
                // A scope disposed without an explicit outcome means the handler threw before
                // settling; report that rather than silently claiming success.
                ErrorMessage = _success ? null : _error ?? "The task ended without reporting an outcome.",
                Summary = _summary,
                CompletedAt = clock.GetCurrentInstant()
            });
        }

        private async Task Publish<T>(string subject, T message)
        {
            try
            {
                await messageBus.PublishAsync(subject, message, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed publishing background run telemetry to {Subject}.", subject);
            }
        }
    }
}
