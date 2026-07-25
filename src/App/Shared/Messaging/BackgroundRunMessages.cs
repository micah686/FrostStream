using NodaTime;

namespace Shared.Messaging;

/// <summary>
/// Live, non-persistent telemetry for scheduled/background task executions. These are core NATS
/// broadcasts (no queue group, no JetStream): the WebAPI hub keeps whatever it observes in memory
/// so the Jobs &gt; Background surface can show what the server is doing right now. Nothing here is
/// authoritative — the durable record of a schedule's outcome stays in scheduling.scheduled_tasks.
/// </summary>
public static class BackgroundRunSubjects
{
    public const string Started = "background.run.started";
    public const string Progress = "background.run.progress";
    public const string Completed = "background.run.completed";
}

public sealed record BackgroundRunStarted
{
    /// <summary>Identifies this one execution. Fresh per run; never reused across retries.</summary>
    public required Guid RunId { get; init; }
    /// <summary>Scheduler task type, e.g. <c>search_reindex</c>.</summary>
    public required string TaskType { get; init; }
    /// <summary>
    /// The owning schedule, or null for work a user kicked off directly (an admin-triggered backup
    /// has no schedule behind it).
    /// </summary>
    public string? ScheduleKey { get; init; }
    /// <summary>Whether a schedule or a person started this run.</summary>
    public required BackgroundRunTrigger Trigger { get; init; }
    public required string IdempotencyKey { get; init; }
    /// <summary>Which service is executing the run (databridge, worker, backupservice).</summary>
    public required string Origin { get; init; }
    /// <summary>Optional one-line context, e.g. "12 sources" or a target channel name.</summary>
    public string? Detail { get; init; }
    public required Instant StartedAt { get; init; }
}

public enum BackgroundRunTrigger
{
    Scheduled = 0,
    Manual = 1
}

public sealed record BackgroundRunProgress
{
    public required Guid RunId { get; init; }
    /// <summary>Human-readable step description shown in the expanded row's log.</summary>
    public required string Message { get; init; }
    /// <summary>Completed units, when the run has countable work.</summary>
    public int? Current { get; init; }
    public int? Total { get; init; }
    /// <summary>0-100. Derived from Current/Total when not supplied explicitly.</summary>
    public double? Percent { get; init; }
    public required Instant OccurredAt { get; init; }
}

public sealed record BackgroundRunCompleted
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    /// <summary>Populated when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Optional closing line, e.g. "Deleted 412 rows".</summary>
    public string? Summary { get; init; }
    public required Instant CompletedAt { get; init; }
}
