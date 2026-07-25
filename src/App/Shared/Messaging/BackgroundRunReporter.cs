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
    public async Task<IBackgroundRunScope> BeginAsync(
        string taskType,
        ScheduledBackgroundRequest request,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var scope = new Scope(messageBus, clock, logger, Guid.NewGuid());
        await Publish(BackgroundRunSubjects.Started, new BackgroundRunStarted
        {
            RunId = scope.RunId,
            TaskType = taskType,
            ScheduleKey = request.ScheduleKey,
            IdempotencyKey = request.IdempotencyKey,
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

/// <summary>No-op reporter for tests and hosts without a message bus.</summary>
public sealed class NullBackgroundRunReporter : IBackgroundRunReporter
{
    public static readonly NullBackgroundRunReporter Instance = new();

    public Task<IBackgroundRunScope> BeginAsync(
        string taskType, ScheduledBackgroundRequest request, string? detail = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IBackgroundRunScope>(new Scope());

    private sealed class Scope : IBackgroundRunScope
    {
        public Guid RunId { get; } = Guid.Empty;
        public Task ReportAsync(string message, int? current = null, int? total = null, double? percent = null) => Task.CompletedTask;
        public void Succeed(string? summary = null) { }
        public void Fail(string errorMessage) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
