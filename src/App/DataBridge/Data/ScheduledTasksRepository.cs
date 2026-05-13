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

    public async Task<IReadOnlyList<ScheduledTaskEntity>> ListOverdueAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        return await db.ScheduledTasks.AsNoTracking()
            .Where(x => x.Enabled && x.CatchupPolicy == ScheduleCatchupPolicy.Coalesce && x.NextDueAt != null && x.NextDueAt <= now)
            .OrderBy(x => x.NextDueAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ScheduledTaskEntity> CreateAsync(ScheduledTaskEntity entity, CancellationToken cancellationToken = default)
    {
        entity.NextDueAt = ComputeNextDue(entity, clock.GetCurrentInstant());
        db.ScheduledTasks.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<ScheduledTaskEntity?> UpdateAsync(ScheduledTaskEntity entity, CancellationToken cancellationToken = default)
    {
        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == entity.Key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        existing.TaskType = entity.TaskType;
        existing.Cron = entity.Cron;
        existing.IntervalSeconds = entity.IntervalSeconds;
        existing.Timezone = entity.Timezone;
        existing.Enabled = entity.Enabled;
        existing.CatchupPolicy = entity.CatchupPolicy;
        existing.NextDueAt = ComputeNextDue(existing, clock.GetCurrentInstant());
        existing.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        db.ScheduledTasks.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkAttemptAsync(string key, Instant attemptedAt, CancellationToken cancellationToken = default)
    {
        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (existing is null)
        {
            return;
        }

        existing.LastAttemptAt = attemptedAt;
        existing.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScheduledTaskEntity?> MarkSuccessAsync(string key, Instant succeededAt, CancellationToken cancellationToken = default)
    {
        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        existing.LastSuccessAt = succeededAt;
        existing.NextDueAt = ComputeNextDue(existing, succeededAt);
        existing.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private static Instant? ComputeNextDue(ScheduledTaskEntity entity, Instant from)
    {
        if (!entity.Enabled)
        {
            return null;
        }

        if (entity.IntervalSeconds is { } intervalSeconds)
        {
            return from.Plus(Duration.FromSeconds(intervalSeconds));
        }

        if (string.IsNullOrWhiteSpace(entity.Cron))
        {
            return null;
        }

        var cron = new CronExpression(entity.Cron)
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(entity.Timezone)
        };
        var next = cron.GetNextValidTimeAfter(from.ToDateTimeOffset());
        return next is null ? null : Instant.FromDateTimeOffset(next.Value);
    }
}
