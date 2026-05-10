using Microsoft.EntityFrameworkCore;
using NodaTime;
using Quartz;
using Shared.Database;

namespace DataBridge.Data;

public sealed class ScheduledTasksRepository(DataBridgeDbContext db, IClock clock) : IScheduledTasksRepository
{
    public Task<ScheduledTaskEntity?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
        => db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, cancellationToken);

    public async Task<IReadOnlyList<ScheduledTaskEntity>> ListAsync(CancellationToken cancellationToken = default)
        => await db.ScheduledTasks.AsNoTracking().OrderBy(x => x.Key).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ScheduledTaskEntity>> ListActiveAsync(CancellationToken cancellationToken = default)
        => await db.ScheduledTasks.AsNoTracking()
            .Where(x => x.Enabled)
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ScheduledTaskEntity>> ListOverdueAsync(Instant now, CancellationToken cancellationToken = default)
        => await db.ScheduledTasks.AsNoTracking()
            .Where(x => x.Enabled && (x.NextDueAt == null || x.NextDueAt < now))
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

    public async Task<ScheduledTaskEntity> CreateAsync(ScheduledTaskEntity entity, CancellationToken cancellationToken = default)
    {
        if (entity.Id == Guid.Empty)
            entity.Id = Guid.NewGuid();

        // Compute first NextDueAt so ListOverdue / Scheduler can reason about it before
        // the schedule has ever fired.
        entity.NextDueAt = ComputeNextDue(entity, fromInstant: clock.GetCurrentInstant());

        db.ScheduledTasks.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<ScheduledTaskEntity?> UpdateAsync(ScheduledTaskEntity patch, CancellationToken cancellationToken = default)
    {
        var entity = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == patch.Key, cancellationToken);
        if (entity is null) return null;

        entity.TaskType = patch.TaskType;
        entity.Cron = patch.Cron;
        entity.IntervalSeconds = patch.IntervalSeconds;
        entity.Timezone = patch.Timezone;
        entity.Enabled = patch.Enabled;
        entity.CatchupPolicy = patch.CatchupPolicy;
        entity.LastUpdated = clock.GetCurrentInstant();

        // Recompute next-due whenever the cadence itself changes.
        entity.NextDueAt = ComputeNextDue(entity, fromInstant: clock.GetCurrentInstant());

        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var entity = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is null) return false;

        db.ScheduledTasks.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ScheduledTaskEntity?> MarkAttemptAsync(string key, Instant attemptedAt, CancellationToken cancellationToken = default)
    {
        var entity = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is null) return null;

        entity.LastAttemptAt = attemptedAt;
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<ScheduledTaskMarkResult?> MarkSuccessAsync(string key, Instant succeededAt, CancellationToken cancellationToken = default)
    {
        var entity = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is null) return null;

        var previous = entity.LastSuccessAt;
        entity.LastSuccessAt = succeededAt;
        entity.NextDueAt = ComputeNextDue(entity, fromInstant: succeededAt);

        await db.SaveChangesAsync(cancellationToken);
        return new ScheduledTaskMarkResult(entity, previous);
    }

    /// <summary>
    /// Compute the next time this schedule should fire after <paramref name="fromInstant"/>,
    /// honouring the schedule's timezone for cron expressions. Returns <see langword="null"/>
    /// when the cron has no future occurrences (rare but possible — e.g. one-shot-style crons).
    /// </summary>
    private static Instant? ComputeNextDue(ScheduledTaskEntity entity, Instant fromInstant)
    {
        if (entity.IntervalSeconds is { } seconds && seconds > 0)
            return fromInstant.Plus(Duration.FromSeconds(seconds));

        if (string.IsNullOrWhiteSpace(entity.Cron))
            return null;

        // Quartz's CronExpression operates on DateTimeOffset and a TimeZoneInfo; convert
        // the NodaTime Instant for the call, convert back to Instant on return.
        var cron = new CronExpression(entity.Cron);
        try
        {
            cron.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(entity.Timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            // Fall back to UTC silently — validation should have caught this upfront.
            cron.TimeZone = TimeZoneInfo.Utc;
        }

        var next = cron.GetNextValidTimeAfter(fromInstant.ToDateTimeOffset());
        return next is { } dto ? Instant.FromDateTimeOffset(dto) : null;
    }
}
