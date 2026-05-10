using NodaTime;
using Shared.Database;

namespace DataBridge.Data;

/// <summary>
/// Result of <see cref="IScheduledTasksRepository.MarkSuccessAsync"/> — the post-mutation
/// snapshot, plus enough context for callers to verify they applied the right window.
/// </summary>
public sealed record ScheduledTaskMarkResult(ScheduledTaskEntity Entity, Instant? PreviousLastSuccessAt);

public interface IScheduledTasksRepository
{
    Task<ScheduledTaskEntity?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledTaskEntity>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledTaskEntity>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns enabled schedules whose <see cref="ScheduledTaskEntity.NextDueAt"/> is in
    /// the past (or null — never run). Used by the Scheduler at startup to publish
    /// catch-up commands.
    /// </summary>
    Task<IReadOnlyList<ScheduledTaskEntity>> ListOverdueAsync(Instant now, CancellationToken cancellationToken = default);

    Task<ScheduledTaskEntity> CreateAsync(ScheduledTaskEntity entity, CancellationToken cancellationToken = default);

    Task<ScheduledTaskEntity?> UpdateAsync(ScheduledTaskEntity patch, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    Task<ScheduledTaskEntity?> MarkAttemptAsync(string key, Instant attemptedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a successful run, advancing <see cref="ScheduledTaskEntity.LastSuccessAt"/>
    /// and <see cref="ScheduledTaskEntity.NextDueAt"/> based on cron / interval.
    /// </summary>
    Task<ScheduledTaskMarkResult?> MarkSuccessAsync(string key, Instant succeededAt, CancellationToken cancellationToken = default);
}
