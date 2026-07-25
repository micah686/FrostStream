namespace BackupService;

internal sealed record BackupJobRecord
{
    public required Guid JobId { get; init; }
    public required string Status { get; init; }
    public required string Name { get; init; }
    public required string Mode { get; init; }
    public required bool Scheduled { get; init; }
    /// <summary>The schedule that triggered this backup; null for admin-triggered runs.</summary>
    public string? ScheduleKey { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? ArchivePath { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
