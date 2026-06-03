using NodaTime;
using Shared.Database;

namespace Shared.Messaging;

public sealed record ScheduledTaskDto
{
    public required int Id { get; init; }
    public required string Key { get; init; }
    public required string TaskType { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public required string Timezone { get; init; }
    public required bool Enabled { get; init; }
    public required ScheduleCatchupPolicy CatchupPolicy { get; init; }
    public Instant? LastAttemptAt { get; init; }
    public Instant? LastSuccessAt { get; init; }
    public ScheduleRunStatus? LastRunStatus { get; init; }
    public Instant? NextDueAt { get; init; }
    public required Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}

public sealed record ScheduleCreateRequestMessage
{
    public required string Key { get; init; }
    public required string TaskType { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public string Timezone { get; init; } = "UTC";
    public bool Enabled { get; init; }
    public ScheduleCatchupPolicy CatchupPolicy { get; init; } = ScheduleCatchupPolicy.Coalesce;
}

public sealed record ScheduleUpdateRequestMessage
{
    public required string Key { get; init; }
    public required string TaskType { get; init; }
    public string? Cron { get; init; }
    public int? IntervalSeconds { get; init; }
    public string Timezone { get; init; } = "UTC";
    public bool Enabled { get; init; }
    public ScheduleCatchupPolicy CatchupPolicy { get; init; } = ScheduleCatchupPolicy.Coalesce;
}

public sealed record ScheduleGetRequestMessage
{
    public required string Key { get; init; }
}

public sealed record ScheduleListRequestMessage;

public sealed record ScheduleListActiveRequestMessage;

public sealed record ScheduleListOverdueRequestMessage;

public sealed record ScheduleDeleteRequestMessage
{
    public required string Key { get; init; }
}

public sealed record ScheduleMarkAttemptRequestMessage
{
    public required string Key { get; init; }
    public required Instant AttemptedAt { get; init; }
}

public sealed record ScheduleMarkSuccessRequestMessage
{
    public required string Key { get; init; }
    public required Instant SucceededAt { get; init; }
}

public sealed record ScheduleMarkFailureRequestMessage
{
    public required string Key { get; init; }
    public required Instant FailedAt { get; init; }
}

public sealed record ScheduleOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public ScheduledTaskDto? Entity { get; init; }
    public IReadOnlyList<ScheduledTaskDto>? Items { get; init; }
}

public enum ScheduleChangeKind
{
    Created = 0,
    Updated = 1,
    Deleted = 2
}

public sealed record ScheduleChangedMessage
{
    public required string Key { get; init; }
    public required ScheduleChangeKind Kind { get; init; }
    public required Instant OccurredAt { get; init; }
}

public sealed record OrphanMetadataCleanupRequested
{
    public required string ScheduleKey { get; init; }
    public required string TaskType { get; init; }
    public required Instant DueWindowUtc { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Instant OccurredAt { get; init; }
}
