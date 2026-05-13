using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Shared.Database;

public enum ScheduleCatchupPolicy
{
    Coalesce = 0,
    Skip = 1
}

public sealed class ScheduledTaskEntity
{
    public int Id { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string TaskType { get; set; }

    [StringLength(255)]
    public string? Cron { get; set; }

    public int? IntervalSeconds { get; set; }

    [Required]
    [StringLength(100)]
    public string Timezone { get; set; } = "UTC";

    public bool Enabled { get; set; }

    public ScheduleCatchupPolicy CatchupPolicy { get; set; } = ScheduleCatchupPolicy.Coalesce;

    public Instant? LastAttemptAt { get; set; }

    public Instant? LastSuccessAt { get; set; }

    public Instant? NextDueAt { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }
}
