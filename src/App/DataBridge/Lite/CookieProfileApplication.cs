using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Lite;

public sealed class CookieProfileApplication(DataBridgeDbContext db, IClock clock)
{
    public async Task<CookieProfileDto> UpsertAsync(
        string ownerSubject,
        string profileKey,
        string? site,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var entity = await db.CookieProfiles.SingleOrDefaultAsync(
            x => x.OwnerSubject == ownerSubject && x.ProfileKey == profileKey,
            cancellationToken);
        if (entity is null)
        {
            entity = new CookieProfileEntity
            {
                Id = Guid.NewGuid(),
                OwnerSubject = ownerSubject,
                ProfileKey = profileKey,
                Site = site,
                DisplayName = displayName,
                CreatedAt = now
            };
            db.CookieProfiles.Add(entity);
        }
        else
        {
            entity.Site = site;
            entity.DisplayName = displayName;
            entity.LastUpdated = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<IReadOnlyList<CookieProfileDto>> ListAsync(
        string ownerSubject,
        CancellationToken cancellationToken)
        => (await db.CookieProfiles.AsNoTracking()
                .Where(x => x.OwnerSubject == ownerSubject)
                .OrderBy(x => x.ProfileKey)
                .ToListAsync(cancellationToken))
            .Select(Map)
            .ToArray();

    public async Task<CookieProfileDto?> GetAsync(
        string ownerSubject,
        string profileKey,
        CancellationToken cancellationToken)
    {
        var entity = await db.CookieProfiles.AsNoTracking().SingleOrDefaultAsync(
            x => x.OwnerSubject == ownerSubject && x.ProfileKey == profileKey,
            cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<bool> DeleteAsync(
        string ownerSubject,
        string profileKey,
        CancellationToken cancellationToken)
    {
        var entity = await db.CookieProfiles.SingleOrDefaultAsync(
            x => x.OwnerSubject == ownerSubject && x.ProfileKey == profileKey,
            cancellationToken);
        if (entity is null)
            return false;
        db.CookieProfiles.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static CookieProfileDto Map(CookieProfileEntity entity) => new()
    {
        Id = entity.Id,
        OwnerSubject = entity.OwnerSubject,
        ProfileKey = entity.ProfileKey,
        Site = entity.Site,
        DisplayName = entity.DisplayName,
        CreatedAt = entity.CreatedAt,
        LastUpdated = entity.LastUpdated
    };
}
