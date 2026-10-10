using System.IO.Hashing;
using System.Text;
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
    /// <summary>
    /// Published by the Scheduler the moment a schedule fires, before the request reaches whichever
    /// service executes it. Guarantees every schedule firing shows up on Jobs &gt; Background even if
    /// the executing service is down or its JetStream consumer is backlogged.
    /// </summary>
    public const string Dispatched = "background.run.dispatched";
    public const string Started = "background.run.started";
    public const string Progress = "background.run.progress";
    public const string Completed = "background.run.completed";
}

/// <summary>
/// Derives the run identifier shared by the Scheduler's dispatch announcement and the executing
/// service's start announcement. Both sides only have the request's idempotency key in common, so
/// the id is a stable hash of it: the executor's "started" event then lands on the same row the
/// scheduler queued rather than opening a second one.
/// </summary>
public static class BackgroundRunIds
{
    public static Guid ForIdempotencyKey(string idempotencyKey)
    {
        var bytes = Encoding.UTF8.GetBytes(idempotencyKey);
        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(bytes, hash);
        return new Guid(hash);
    }
}

/// <summary>
/// Announces that a schedule fired and its request was handed to the bus. The run sits in
/// <c>queued</c> until the executing service publishes <see cref="BackgroundRunStarted"/> for the
/// same <see cref="RunId"/>.
/// </summary>
public sealed record BackgroundRunDispatched
{
    /// <summary>Derived from <see cref="IdempotencyKey"/> so the executor reports against this same run.</summary>
    public required Guid RunId { get; init; }
    public required string TaskType { get; init; }
    public required string ScheduleKey { get; init; }
    public required BackgroundRunTrigger Trigger { get; init; }
    public required string IdempotencyKey { get; init; }
    /// <summary>Always <c>scheduler</c> today; the executing service overwrites it on start.</summary>
    public required string Origin { get; init; }
    public string? Detail { get; init; }
    /// <summary>The occurrence this firing covers, which is what the idempotency key is keyed on.</summary>
    public required Instant DueWindowUtc { get; init; }
    public required Instant DispatchedAt { get; init; }
}

public sealed record BackgroundRunStarted
{
    /// <summary>
    /// Identifies this one execution. Scheduled runs derive it from the idempotency key
    /// (see <see cref="BackgroundRunIds"/>) so the start lands on the row the Scheduler already
    /// queued — a JetStream redelivery of the same occurrence therefore reuses the row rather than
    /// stacking a second one. Manual runs get a fresh id each time.
    /// </summary>
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
