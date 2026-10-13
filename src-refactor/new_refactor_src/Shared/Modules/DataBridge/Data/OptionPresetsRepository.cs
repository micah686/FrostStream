using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;

namespace DataBridge.Data;

public sealed class OptionPresetsRepository(DataBridgeDbContext db, IClock clock) : IOptionPresetsRepository
{
    public Task<OptionPresetEntity?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
        => db.OptionPresets.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, cancellationToken);

    public async Task<IReadOnlyList<OptionPresetEntity>> ListAsync(CancellationToken cancellationToken = default)
        => await db.OptionPresets.AsNoTracking()
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

    public async Task<OptionPresetEntity> CreateAsync(
        string key,
        string name,
        string? description,
        string ytdlpOptionsJson,
        CancellationToken cancellationToken = default)
    {
        var entity = new OptionPresetEntity
        {
            Key = key,
            Name = name,
            Description = description,
            YtDlpOptionsJson = ytdlpOptionsJson
        };
        db.OptionPresets.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<OptionPresetEntity?> UpdateAsync(
        string key,
        string name,
        string? description,
        string ytdlpOptionsJson,
        CancellationToken cancellationToken = default)
    {
        var entity = await db.OptionPresets.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is null) return null;

        entity.Name = name;
        entity.Description = description;
        entity.YtDlpOptionsJson = ytdlpOptionsJson;
        entity.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var entity = await db.OptionPresets.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is null) return false;

        db.OptionPresets.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
