using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;

namespace DataBridge.Data;

public sealed class UserPlaylistsRepository(DataBridgeDbContext db, IClock clock) : IUserPlaylistsRepository
{
    private const int PositionOffset = 1_000_000;

    public async Task<UserPlaylistDetail> CreateAsync(
        string ownerSubject,
        string name,
        string? description,
        CancellationToken ct = default)
    {
        var entity = new UserPlaylistEntity
        {
            PlaylistId = Guid.NewGuid(),
            OwnerSubject = ownerSubject,
            Name = name.Trim(),
            Description = NormalizeDescription(description)
        };

        db.UserPlaylists.Add(entity);
        await db.SaveChangesAsync(ct);
        return new UserPlaylistDetail(entity, []);
    }

    public async Task<UserPlaylistDetail?> UpdateAsync(
        string ownerSubject,
        Guid playlistId,
        string name,
        string? description,
        CancellationToken ct = default)
    {
        var playlist = await db.UserPlaylists
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.OwnerSubject == ownerSubject, ct);
        if (playlist is null)
            return null;

        playlist.Name = name.Trim();
        playlist.Description = NormalizeDescription(description);
        playlist.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);

        return await GetAsync(ownerSubject, playlistId, ct);
    }

    public async Task<bool> DeleteAsync(string ownerSubject, Guid playlistId, CancellationToken ct = default)
    {
        var playlist = await db.UserPlaylists
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.OwnerSubject == ownerSubject, ct);
        if (playlist is null)
            return false;

        db.UserPlaylists.Remove(playlist);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<UserPlaylistDetail?> GetAsync(string ownerSubject, Guid playlistId, CancellationToken ct = default)
    {
        var playlist = await db.UserPlaylists
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.OwnerSubject == ownerSubject, ct);
        if (playlist is null)
            return null;

        var items = await db.UserPlaylistItems
            .AsNoTracking()
            .Where(x => x.PlaylistId == playlistId)
            .OrderBy(x => x.Position)
            .ToListAsync(ct);

        return new UserPlaylistDetail(playlist, items);
    }

    public async Task<IReadOnlyList<UserPlaylistSummary>> ListAsync(
        string ownerSubject,
        int pageSize,
        int pageOffset,
        CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var offset = Math.Max(0, pageOffset);

        var playlists = await db.UserPlaylists
            .AsNoTracking()
            .Where(x => x.OwnerSubject == ownerSubject)
            .OrderByDescending(x => x.CreatedAt)
            .Skip(offset)
            .Take(size)
            .ToListAsync(ct);

        if (playlists.Count == 0)
            return [];

        var ids = playlists.Select(x => x.PlaylistId).ToArray();
        var counts = await db.UserPlaylistItems
            .AsNoTracking()
            .Where(x => ids.Contains(x.PlaylistId))
            .GroupBy(x => x.PlaylistId)
            .Select(g => new { PlaylistId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var countById = counts.ToDictionary(x => x.PlaylistId, x => x.Count);

        return playlists
            .Select(x => new UserPlaylistSummary(x, countById.GetValueOrDefault(x.PlaylistId)))
            .ToArray();
    }

    public async Task<UserPlaylistMutationResult> AddItemAsync(
        string ownerSubject,
        Guid playlistId,
        Guid mediaGuid,
        int? position,
        CancellationToken ct = default)
    {
        var playlist = await db.UserPlaylists
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.OwnerSubject == ownerSubject, ct);
        if (playlist is null)
            return UserPlaylistMutationResult.Fail("not_found", $"Playlist '{playlistId}' was not found.");

        if (!await db.Media.AnyAsync(x => x.MediaGuid == mediaGuid, ct))
            return UserPlaylistMutationResult.Fail("media_not_found", $"Media '{mediaGuid}' was not found.");

        if (await db.UserPlaylistItems.AnyAsync(x => x.PlaylistId == playlistId && x.MediaGuid == mediaGuid, ct))
            return UserPlaylistMutationResult.Fail("duplicate", $"Media '{mediaGuid}' is already in the playlist.");

        var itemCount = await db.UserPlaylistItems.CountAsync(x => x.PlaylistId == playlistId, ct);
        var targetPosition = Math.Clamp(position ?? itemCount + 1, 1, itemCount + 1);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ShiftPositionsUpAsync(playlistId, targetPosition, ct);
        db.UserPlaylistItems.Add(new UserPlaylistItemEntity
        {
            PlaylistId = playlistId,
            MediaGuid = mediaGuid,
            Position = targetPosition
        });
        playlist.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return UserPlaylistMutationResult.Ok((await GetAsync(ownerSubject, playlistId, ct))!);
    }

    public async Task<UserPlaylistMutationResult> RemoveItemAsync(
        string ownerSubject,
        Guid playlistId,
        Guid mediaGuid,
        CancellationToken ct = default)
    {
        var playlist = await db.UserPlaylists
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.OwnerSubject == ownerSubject, ct);
        if (playlist is null)
            return UserPlaylistMutationResult.Fail("not_found", $"Playlist '{playlistId}' was not found.");

        var item = await db.UserPlaylistItems
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.MediaGuid == mediaGuid, ct);
        if (item is null)
            return UserPlaylistMutationResult.Fail("item_not_found", $"Media '{mediaGuid}' is not in the playlist.");

        var removedPosition = item.Position;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.UserPlaylistItems.Remove(item);
        await db.SaveChangesAsync(ct);
        await ShiftPositionsDownAsync(playlistId, removedPosition, ct);
        playlist.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return UserPlaylistMutationResult.Ok((await GetAsync(ownerSubject, playlistId, ct))!);
    }

    public async Task<UserPlaylistMutationResult> ReorderItemsAsync(
        string ownerSubject,
        Guid playlistId,
        IReadOnlyList<Guid> mediaGuids,
        CancellationToken ct = default)
    {
        var playlist = await db.UserPlaylists
            .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.OwnerSubject == ownerSubject, ct);
        if (playlist is null)
            return UserPlaylistMutationResult.Fail("not_found", $"Playlist '{playlistId}' was not found.");

        var items = await db.UserPlaylistItems
            .Where(x => x.PlaylistId == playlistId)
            .OrderBy(x => x.Position)
            .ToListAsync(ct);

        if (mediaGuids.Count != items.Count || mediaGuids.Distinct().Count() != mediaGuids.Count)
            return UserPlaylistMutationResult.Fail("invalid_order", "The order must contain each playlist media item exactly once.");

        var current = items.Select(x => x.MediaGuid).ToHashSet();
        if (!mediaGuids.All(current.Contains))
            return UserPlaylistMutationResult.Fail("invalid_order", "The order contains media that is not in the playlist.");

        var byMediaGuid = items.ToDictionary(x => x.MediaGuid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        for (var i = 0; i < mediaGuids.Count; i++)
            byMediaGuid[mediaGuids[i]].Position = -(i + 1);
        await db.SaveChangesAsync(ct);

        for (var i = 0; i < mediaGuids.Count; i++)
            byMediaGuid[mediaGuids[i]].Position = i + 1;

        playlist.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return UserPlaylistMutationResult.Ok((await GetAsync(ownerSubject, playlistId, ct))!);
    }

    private async Task ShiftPositionsUpAsync(Guid playlistId, int fromPosition, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE playlists.user_playlist_items
             SET position = position + {PositionOffset}
             WHERE playlist_id = {playlistId} AND position >= {fromPosition}
             """,
            ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE playlists.user_playlist_items
             SET position = position - {PositionOffset} + 1
             WHERE playlist_id = {playlistId} AND position >= {PositionOffset}
             """,
            ct);
    }

    private async Task ShiftPositionsDownAsync(Guid playlistId, int afterPosition, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE playlists.user_playlist_items
             SET position = position + {PositionOffset}
             WHERE playlist_id = {playlistId} AND position > {afterPosition}
             """,
            ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE playlists.user_playlist_items
             SET position = position - {PositionOffset} - 1
             WHERE playlist_id = {playlistId} AND position >= {PositionOffset}
             """,
            ct);
    }

    private static string? NormalizeDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
