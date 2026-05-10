using NodaTime;

namespace Shared.Messaging;

/// <summary>Wire enum mirror of <c>Shared.Database.ScheduleCatchupPolicy</c>.</summary>
public enum ScheduleCatchupPolicyDto
{
    Coalesce = 0,
    Skip = 1
}

public sealed class ScheduledTaskDto
{
    public Guid Id { get; init; }
    public required string Key { get; init; }
    public required string TaskType { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public required string Timezone { get; init; }
    public bool Enabled { get; init; }
    public ScheduleCatchupPolicyDto CatchupPolicy { get; init; }
    public Instant? LastAttemptAt { get; init; }
    public Instant? LastSuccessAt { get; init; }
    public Instant? NextDueAt { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}

public sealed class ScheduleCreateRequestMessage
{
    public required string Key { get; init; }
    public required string TaskType { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public string Timezone { get; init; } = "UTC";
    public bool Enabled { get; init; } = true;
    public ScheduleCatchupPolicyDto CatchupPolicy { get; init; } = ScheduleCatchupPolicyDto.Coalesce;
}

public sealed class ScheduleUpdateRequestMessage
{
    public required string Key { get; init; }
    public required string TaskType { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public string Timezone { get; init; } = "UTC";
    public bool Enabled { get; init; } = true;
    public ScheduleCatchupPolicyDto CatchupPolicy { get; init; } = ScheduleCatchupPolicyDto.Coalesce;
}

public sealed class ScheduleGetRequestMessage
{
    public required string Key { get; init; }
}

public sealed class ScheduleListRequestMessage;

public sealed class ScheduleListActiveRequestMessage;

public sealed class ScheduleListOverdueRequestMessage;

public sealed class ScheduleDeleteRequestMessage
{
    public required string Key { get; init; }
}

public sealed class ScheduleMarkAttemptRequestMessage
{
    public required string Key { get; init; }
    public required Instant AttemptedAt { get; init; }
}

public sealed class ScheduleMarkSuccessRequestMessage
{
    public required string Key { get; init; }
    public required Instant SucceededAt { get; init; }
    /// <summary>Idempotency key from the trigger command, for tracing/dedupe.</summary>
    public string? IdempotencyKey { get; init; }
}

public sealed class ScheduleOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public ScheduledTaskDto? Entity { get; init; }
    public IReadOnlyList<ScheduledTaskDto>? Items { get; init; }
}

public enum ScheduleChangeKind
{
    Created,
    Updated,
    Deleted
}

public sealed class ScheduleChangedMessage
{
    public required string Key { get; init; }
    public required ScheduleChangeKind Change { get; init; }
}

/// <summary>Trigger command published by Scheduler when an orphan-cleanup schedule fires.</summary>
public sealed class OrphanMetadataCleanupRequested
{
    /// <summary>The <see cref="ScheduledTaskDto.Key"/> of the schedule that fired.</summary>
    public required string ScheduleKey { get; init; }

    /// <summary>End-to-end correlation id for tracing.</summary>
    public required Guid CorrelationId { get; init; }

    /// <summary>
    /// Deterministic idempotency key
    /// (<c>{task_type}:{schedule_key}:{due_window_utc_iso}</c>). Set as the
    /// JetStream <c>Nats-Msg-Id</c> header on publish so stream-level dedupe holds.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    public required Instant TriggeredAt { get; init; }
}
