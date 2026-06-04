using System.ComponentModel.DataAnnotations;
using NodaTime;
using Quartz;
using Shared.Database;

namespace WebAPI.Features.Schedules.Models;

public abstract class ScheduleRequestBase : IValidatableObject
{
    [StringLength(255)]
    public string? Cron { get; init; }

    [Range(1, int.MaxValue)]
    public int? IntervalSeconds { get; init; }

    [Required]
    [StringLength(100)]
    public string Timezone { get; init; } = "UTC";

    public bool Enabled { get; init; }

    public ScheduleCatchupPolicy CatchupPolicy { get; init; } = ScheduleCatchupPolicy.Coalesce;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var hasCron = !string.IsNullOrWhiteSpace(Cron);
        var hasInterval = IntervalSeconds is not null;
        if (hasCron == hasInterval)
        {
            yield return new ValidationResult(
                "Exactly one of cron or intervalSeconds must be supplied.",
                [nameof(Cron), nameof(IntervalSeconds)]);
        }

        if (hasCron && !CronExpression.IsValidExpression(Cron!))
        {
            yield return new ValidationResult("Cron must be a valid Quartz cron expression.", [nameof(Cron)]);
        }

        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(Timezone) is null)
        {
            yield return new ValidationResult("Timezone must be a valid TZDB timezone id.", [nameof(Timezone)]);
        }
    }
}

public sealed class ScheduleCreateRequest : ScheduleRequestBase
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string TaskType { get; init; }
}

public sealed class ScheduleUpdateRequest : ScheduleRequestBase
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string TaskType { get; init; }
}

public sealed class ScheduledTaskResponse
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
    public Instant? NextDueAt { get; init; }
    public required Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
