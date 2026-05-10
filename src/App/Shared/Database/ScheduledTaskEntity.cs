using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NodaTime;

namespace Shared.Database;

/// <summary>
/// Catch-up policy applied when a Scheduler restart finds an overdue schedule.
/// </summary>
public enum ScheduleCatchupPolicy
{
    /// <summary>Enqueue exactly one catch-up command for the most-recent missed window, ignore older ones.</summary>
    Coalesce = 0,
    /// <summary>Skip all missed runs; resume the normal cadence.</summary>
    Skip = 1
}

/// <summary>
/// A persisted, named schedule. Owned by DataBridge; consumed by the Scheduler service
/// at startup (and via <c>fs.schedules.changed</c> events afterwards) to build
/// in-memory Quartz triggers. Either <see cref="Cron"/> or <see cref="IntervalSeconds"/>
/// must be set, never both.
/// </summary>
public class ScheduledTaskEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; set; }

    /// <summary>Discriminator string the Scheduler maps to a registered Quartz <c>IJob</c>.</summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string TaskType { get; set; }

    /// <summary>Quartz/Cronos-compatible cron expression. Mutually exclusive with <see cref="IntervalSeconds"/>.</summary>
    public string? Cron { get; set; }

    /// <summary>Fixed interval in seconds. Mutually exclusive with <see cref="Cron"/>.</summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>IANA timezone name; <c>UTC</c> by default.</summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Timezone { get; set; } = "UTC";

    public bool Enabled { get; set; } = true;

    public ScheduleCatchupPolicy CatchupPolicy { get; set; } = ScheduleCatchupPolicy.Coalesce;

    public Instant? LastAttemptAt { get; set; }
    public Instant? LastSuccessAt { get; set; }
    public Instant? NextDueAt { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }
}
