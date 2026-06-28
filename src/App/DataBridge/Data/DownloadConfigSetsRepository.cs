using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;

namespace DataBridge.Data;

public sealed class DownloadConfigSetsRepository(DataBridgeDbContext db, IClock clock) : IDownloadConfigSetsRepository
{
    public Task<DownloadConfigSetEntity?> GetAsync(string ownerSubject, string key, CancellationToken cancellationToken = default)
        => db.DownloadConfigSets
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OwnerSubject == ownerSubject && x.Key == key, cancellationToken);

    public async Task<IReadOnlyList<DownloadConfigSetEntity>> ListAsync(string ownerSubject, CancellationToken cancellationToken = default)
        => await db.DownloadConfigSets
            .AsNoTracking()
            .Where(x => x.OwnerSubject == ownerSubject)
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);

    public async Task<DownloadConfigSetEntity> CreateAsync(DownloadConfigSetEntity entity, CancellationToken cancellationToken = default)
    {
        db.DownloadConfigSets.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<DownloadConfigSetEntity?> UpdateAsync(DownloadConfigSetEntity entity, CancellationToken cancellationToken = default)
    {
        var existing = await db.DownloadConfigSets
            .FirstOrDefaultAsync(x => x.OwnerSubject == entity.OwnerSubject && x.Key == entity.Key, cancellationToken);
        if (existing is null)
            return null;

        existing.Name = entity.Name;
        existing.Description = entity.Description;
        existing.StorageKey = entity.StorageKey;
        existing.CookieProfileKey = entity.CookieProfileKey;
        existing.YtDlpOptionsJson = entity.YtDlpOptionsJson;
        existing.IgnoreKeywordsJson = entity.IgnoreKeywordsJson;
        existing.EncodeForPlaylist = entity.EncodeForPlaylist;
        existing.AudioFormat = entity.AudioFormat;
        existing.Priority = entity.Priority;
        existing.FetchComments = entity.FetchComments;
        existing.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAsync(string ownerSubject, string key, CancellationToken cancellationToken = default)
    {
        var existing = await db.DownloadConfigSets
            .FirstOrDefaultAsync(x => x.OwnerSubject == ownerSubject && x.Key == key, cancellationToken);
        if (existing is null)
            return false;

        db.DownloadConfigSets.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
