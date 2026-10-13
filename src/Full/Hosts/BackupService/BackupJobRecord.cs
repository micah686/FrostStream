namespace BackupService;

internal static class BackupJobKinds
{
    public const string Backup = "backup";
    public const string VerifyQuick = "verify-quick";
    public const string VerifyDeep = "verify-deep";
    public const string Restore = "restore";
}

internal sealed record BackupJobRecord
{
    public required Guid JobId { get; init; }
    public required string Status { get; init; }
    /// <summary>One of <see cref="BackupJobKinds"/>.</summary>
    public required string Kind { get; init; }
    /// <summary>User-supplied backup name (backup kind only); bound to the pgBackRest label via annotation.</summary>
    public string? Name { get; init; }
    /// <summary>Backup type, "full" or "diff" (backup kind only).</summary>
    public string? Type { get; init; }
    /// <summary>pgBackRest backup label: produced by a backup, targeted by a verify/restore.</summary>
    public string? Label { get; init; }
    /// <summary>Point-in-time recovery target (restore kind only).</summary>
    public DateTimeOffset? TargetTime { get; init; }
    public required bool Scheduled { get; init; }
    /// <summary>The schedule that triggered this backup; null for admin-triggered runs.</summary>
    public string? ScheduleKey { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
