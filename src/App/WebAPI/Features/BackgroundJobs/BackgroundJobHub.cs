using System.Collections.Concurrent;
using System.Threading.Channels;
using Conduit.NATS;
using NodaTime;
using Shared.Messaging;

namespace WebAPI.Features.BackgroundJobs;

/// <summary>One item on the background-run SSE stream.</summary>
public abstract record BackgroundRunStreamEvent
{
    public abstract Guid RunId { get; }

    /// <summary>A schedule fired; the run is waiting for a service to pick it up.</summary>
    public sealed record Queued(BackgroundRunView Value) : BackgroundRunStreamEvent
    {
        public override Guid RunId => Value.RunId;
    }

    public sealed record Started(BackgroundRunView Value) : BackgroundRunStreamEvent
    {
        public override Guid RunId => Value.RunId;
    }

    public sealed record Progress(BackgroundRunProgress Value) : BackgroundRunStreamEvent
    {
        public override Guid RunId => Value.RunId;
    }

    public sealed record Completed(BackgroundRunView Value) : BackgroundRunStreamEvent
    {
        public override Guid RunId => Value.RunId;
    }
}

/// <summary>A single log line captured for a run.</summary>
public sealed record BackgroundRunLogLine(string Message, Instant At);

/// <summary>The hub's in-memory view of one background execution.</summary>
public sealed record BackgroundRunView
{
    /// <summary>Statuses a run row can carry, in lifecycle order.</summary>
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public required Guid RunId { get; init; }
    public required string TaskType { get; init; }
    public string? ScheduleKey { get; init; }
    public required BackgroundRunTrigger Trigger { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Origin { get; init; }
    public string? Detail { get; init; }
    /// <summary>For a queued run this is the moment the schedule fired; the executor overwrites it on start.</summary>
    public required Instant StartedAt { get; init; }
    /// <summary>Set when the Scheduler announced the firing, so the wait before pickup stays visible.</summary>
    public Instant? QueuedAt { get; init; }

    public string Status { get; init; } = Running;
    public string? Message { get; init; }
    public int? Current { get; init; }
    public int? Total { get; init; }
    public double? Percent { get; init; }
    public Instant? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<BackgroundRunLogLine> Log { get; init; } = [];
}

/// <summary>
/// Singleton background service that mirrors live scheduled/background task telemetry into memory
/// and fans it to browser SSE. Deliberately volatile — the list is rebuilt only from events observed
/// since this process started, so a server restart clears it (the durable per-schedule outcome lives
/// in scheduling.scheduled_tasks, surfaced by the Schedules admin page instead).
///
/// A run enters as <c>queued</c> when the Scheduler reports the firing, flips to <c>running</c> when
/// the executing service starts it, and settles as <c>completed</c>/<c>failed</c>. Both sides derive
/// the run id from the request's idempotency key, so the executor's events update the queued row.
///
/// Completed runs linger briefly so a run that finishes while you are looking at it does not vanish
/// mid-read; they are then trimmed to <see cref="MaxCompletedRetained"/> newest. A queued run nobody
/// ever picks up is held for <see cref="QueuedRetention"/> — long enough to be noticed as stuck.
/// </summary>
public sealed class BackgroundJobHub(IMessageBus messageBus, IClock clock, ILogger<BackgroundJobHub> logger)
    : BackgroundService
{
    private const int MaxLogLines = 200;
    private const int MaxCompletedRetained = 25;
    private static readonly Duration CompletedRetention = Duration.FromMinutes(15);
    private static readonly Duration QueuedRetention = Duration.FromHours(1);

    private readonly ConcurrentDictionary<Guid, BackgroundRunView> _runs = new();
    private readonly ConcurrentDictionary<Guid, Channel<BackgroundRunStreamEvent>> _subscribers = new();
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<BackgroundRunDispatched>(
            BackgroundRunSubjects.Dispatched, HandleDispatchedAsync, queueGroup: null, cancellationToken: stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<BackgroundRunStarted>(
            BackgroundRunSubjects.Started, HandleStartedAsync, queueGroup: null, cancellationToken: stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<BackgroundRunProgress>(
            BackgroundRunSubjects.Progress, HandleProgressAsync, queueGroup: null, cancellationToken: stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<BackgroundRunCompleted>(
            BackgroundRunSubjects.Completed, HandleCompletedAsync, queueGroup: null, cancellationToken: stoppingToken));

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }
        _subscriptions.Clear();

        foreach (var (_, channel) in _subscribers)
            channel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Current snapshot: active runs first (running before queued, oldest first within each),
    /// then recently finished ones newest-first.
    /// </summary>
    public IReadOnlyList<BackgroundRunView> List()
    {
        Prune();
        return _runs.Values
            .OrderBy(x => x.Status switch
            {
                BackgroundRunView.Running => 0,
                BackgroundRunView.Queued => 1,
                _ => 2
            })
            .ThenBy(x => IsActive(x) ? x.StartedAt : Instant.MaxValue)
            .ThenByDescending(x => x.CompletedAt ?? x.StartedAt)
            .ToArray();
    }

    private static bool IsActive(BackgroundRunView run)
        => run.Status is BackgroundRunView.Running or BackgroundRunView.Queued;

    public (Guid Id, ChannelReader<BackgroundRunStreamEvent> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<BackgroundRunStreamEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    private Task HandleDispatchedAsync(IMessageContext<BackgroundRunDispatched> context)
    {
        var m = context.Message;
        // A redelivery of the same firing (or a Scheduler restart replaying it) must not reset a run
        // that is already under way, so only an unseen id opens a queued row.
        if (_runs.ContainsKey(m.RunId))
            return Task.CompletedTask;

        var view = new BackgroundRunView
        {
            RunId = m.RunId,
            TaskType = m.TaskType,
            ScheduleKey = m.ScheduleKey,
            Trigger = m.Trigger,
            IdempotencyKey = m.IdempotencyKey,
            Origin = m.Origin,
            Detail = m.Detail,
            StartedAt = m.DispatchedAt,
            QueuedAt = m.DispatchedAt,
            Status = BackgroundRunView.Queued,
            Message = "Waiting for a service to pick this up…",
            Log = [new BackgroundRunLogLine("Schedule fired; request queued.", m.DispatchedAt)]
        };
        _runs[m.RunId] = view;
        Prune();
        Fan(new BackgroundRunStreamEvent.Queued(view));
        return Task.CompletedTask;
    }

    private Task HandleStartedAsync(IMessageContext<BackgroundRunStarted> context)
    {
        var m = context.Message;
        // Scheduled runs share the Scheduler's run id, so a queued row already exists; keep its log
        // (and the moment it was queued) rather than starting the row's history over.
        _runs.TryGetValue(m.RunId, out var queued);
        var view = new BackgroundRunView
        {
            RunId = m.RunId,
            TaskType = m.TaskType,
            ScheduleKey = m.ScheduleKey ?? queued?.ScheduleKey,
            Trigger = m.Trigger,
            IdempotencyKey = m.IdempotencyKey,
            Origin = m.Origin,
            Detail = m.Detail ?? queued?.Detail,
            StartedAt = m.StartedAt,
            QueuedAt = queued?.QueuedAt,
            Status = BackgroundRunView.Running,
            Log = queued is null
                ? []
                : Append(queued.Log, new BackgroundRunLogLine($"Picked up by {m.Origin}.", m.StartedAt))
        };
        _runs[m.RunId] = view;
        Prune();
        Fan(new BackgroundRunStreamEvent.Started(view));
        return Task.CompletedTask;
    }

    private Task HandleProgressAsync(IMessageContext<BackgroundRunProgress> context)
    {
        var m = context.Message;
        // A progress frame for a run this process never saw start (e.g. the run began before this
        // server booted) has no row to attach to; drop it rather than inventing a partial run.
        if (!_runs.TryGetValue(m.RunId, out var existing))
            return Task.CompletedTask;

        _runs[m.RunId] = existing with
        {
            Message = m.Message,
            Current = m.Current ?? existing.Current,
            Total = m.Total ?? existing.Total,
            Percent = m.Percent ?? existing.Percent,
            Log = Append(existing.Log, new BackgroundRunLogLine(m.Message, m.OccurredAt))
        };
        Fan(new BackgroundRunStreamEvent.Progress(m));
        return Task.CompletedTask;
    }

    private Task HandleCompletedAsync(IMessageContext<BackgroundRunCompleted> context)
    {
        var m = context.Message;
        if (!_runs.TryGetValue(m.RunId, out var existing))
            return Task.CompletedTask;

        var closing = m.Summary ?? (m.Success ? "Completed." : m.ErrorMessage ?? "Failed.");
        var view = existing with
        {
            Status = m.Success ? BackgroundRunView.Completed : BackgroundRunView.Failed,
            CompletedAt = m.CompletedAt,
            ErrorMessage = m.ErrorMessage,
            Summary = m.Summary,
            Percent = m.Success ? 100 : existing.Percent,
            Message = closing,
            Log = Append(existing.Log, new BackgroundRunLogLine(closing, m.CompletedAt))
        };
        _runs[m.RunId] = view;
        Fan(new BackgroundRunStreamEvent.Completed(view));
        return Task.CompletedTask;
    }

    private static IReadOnlyList<BackgroundRunLogLine> Append(IReadOnlyList<BackgroundRunLogLine> log, BackgroundRunLogLine line)
    {
        var appended = new List<BackgroundRunLogLine>(log) { line };
        return appended.Count > MaxLogLines
            ? appended.GetRange(appended.Count - MaxLogLines, MaxLogLines)
            : appended;
    }

    /// <summary>
    /// Drops finished runs once they age out, keeping only the newest few for context, and lets go of
    /// queued runs no service ever claimed so a dead consumer does not pin a row indefinitely.
    /// </summary>
    private void Prune()
    {
        var now = clock.GetCurrentInstant();
        var finished = _runs.Values
            .Where(x => !IsActive(x) && x.CompletedAt is not null)
            .OrderByDescending(x => x.CompletedAt!.Value)
            .ToArray();

        for (var i = 0; i < finished.Length; i++)
        {
            var run = finished[i];
            if (i >= MaxCompletedRetained || now - run.CompletedAt!.Value > CompletedRetention)
                _runs.TryRemove(run.RunId, out _);
        }

        foreach (var run in _runs.Values)
        {
            if (run.Status == BackgroundRunView.Queued && now - run.StartedAt > QueuedRetention)
                _runs.TryRemove(run.RunId, out _);
        }
    }

    private void Fan(BackgroundRunStreamEvent evt)
    {
        foreach (var (id, channel) in _subscribers)
        {
            if (!channel.Writer.TryWrite(evt))
                logger.LogTrace("Dropped background run event for {RunId} (subscriber {Id} channel full).", evt.RunId, id);
        }
    }
}
