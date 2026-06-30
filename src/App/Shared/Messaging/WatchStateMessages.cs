using NodaTime;

namespace Shared.Messaging;

public static class WatchStateSubjects
{
    public const string Upsert = "media.watch-state.upsert";
    public const string Get = "media.watch-state.get";
}

public sealed record WatchStateUpsertRequest
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool Completed { get; init; }
}

public sealed record WatchStateGetRequest
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
}

public sealed record WatchStateResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public WatchStateDto? State { get; init; }
}

public sealed record WatchStateDto
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool Completed { get; init; }
    public Instant? WatchedAt { get; init; }
    public required Instant LastPlayedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
}

public static class WatchedAutoDeleteSubjects
{
    public const string GetPolicy = "media.watched-auto-delete.policy.get";
    public const string UpdatePolicy = "media.watched-auto-delete.policy.update";
    public const string RunCleanup = "media.watched-auto-delete.cleanup.run";
}

public sealed record WatchedAutoDeletePolicyGetRequest;

public sealed record WatchedAutoDeletePolicyUpdateRequest
{
    public required bool Enabled { get; init; }
    public required int DeleteAfterDays { get; init; }
    public required int MaxDeletionsPerRun { get; init; }
    public string? UpdatedBy { get; init; }
}

public sealed record WatchedAutoDeleteCleanupRunRequest;

public sealed record WatchedAutoDeletePolicyResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public WatchedAutoDeletePolicyDto? Policy { get; init; }
}

public sealed record WatchedAutoDeleteCleanupResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public WatchedAutoDeleteCleanupResultDto? Result { get; init; }
}

public sealed record WatchedAutoDeletePolicyDto
{
    public required bool Enabled { get; init; }
    public required int DeleteAfterDays { get; init; }
    public required int MaxDeletionsPerRun { get; init; }
    public string? UpdatedBy { get; init; }
    public Instant? UpdatedAt { get; init; }
    public Instant? LastRunAt { get; init; }
    public int LastDeletedCount { get; init; }
    public int LastFailedCount { get; init; }
}

public sealed record WatchedAutoDeleteCleanupResultDto
{
    public required bool PolicyEnabled { get; init; }
    public required Instant? Cutoff { get; init; }
    public required int CandidatesFound { get; init; }
    public required int DeletedCount { get; init; }
    public required int FailedCount { get; init; }
    public required int FilesDeleted { get; init; }
}

