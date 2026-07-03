using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public sealed class UserNotesRepository(DataBridgeDbContext db, IClock clock) : IUserNotesRepository
{
    private const int MaxNoteLength = 8192;

    public async Task<UserNoteMutationResult> UpsertAsync(
        string ownerSubject,
        string targetType,
        string targetId,
        string note,
        CancellationToken ct = default)
    {
        var normalized = await NormalizeAndValidateAsync(ownerSubject, targetType, targetId, ct);
        if (!normalized.Success)
            return UserNoteMutationResult.Fail(normalized.ErrorCode!, normalized.ErrorMessage!);

        var normalizedNote = NormalizeNote(note);
        if (normalizedNote is null)
            return UserNoteMutationResult.Fail("validation", "note is required.");

        var existing = await db.UserNotes.FirstOrDefaultAsync(x =>
            x.OwnerSubject == ownerSubject &&
            x.TargetType == normalized.TargetType &&
            x.TargetId == normalized.TargetId, ct);

        if (existing is null)
        {
            existing = new UserNoteEntity
            {
                OwnerSubject = ownerSubject.Trim(),
                TargetType = normalized.TargetType!,
                TargetId = normalized.TargetId!,
                Note = normalizedNote
            };
            db.UserNotes.Add(existing);
        }
        else
        {
            existing.Note = normalizedNote;
            existing.UpdatedAt = clock.GetCurrentInstant();
        }

        await db.SaveChangesAsync(ct);
        return UserNoteMutationResult.Ok(existing);
    }

    public async Task<UserNoteEntity?> GetAsync(
        string ownerSubject,
        string targetType,
        string targetId,
        CancellationToken ct = default)
    {
        var normalized = NormalizeTarget(targetType, targetId);
        if (string.IsNullOrWhiteSpace(ownerSubject) || normalized is null)
            return null;

        return await db.UserNotes
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.OwnerSubject == ownerSubject &&
                x.TargetType == normalized.Value.TargetType &&
                x.TargetId == normalized.Value.TargetId, ct);
    }

    public async Task<bool> DeleteAsync(
        string ownerSubject,
        string targetType,
        string targetId,
        CancellationToken ct = default)
    {
        var normalized = NormalizeTarget(targetType, targetId);
        if (string.IsNullOrWhiteSpace(ownerSubject) || normalized is null)
            return false;

        var existing = await db.UserNotes.FirstOrDefaultAsync(x =>
            x.OwnerSubject == ownerSubject &&
            x.TargetType == normalized.Value.TargetType &&
            x.TargetId == normalized.Value.TargetId, ct);
        if (existing is null)
            return false;

        db.UserNotes.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<UserNoteSearchResult> SearchAsync(
        string ownerSubject,
        string query,
        string? targetType,
        int pageSize,
        int pageOffset,
        CancellationToken ct = default)
    {
        var normalizedType = string.IsNullOrWhiteSpace(targetType)
            ? null
            : UserNoteTargetTypes.Normalize(targetType);
        if (!string.IsNullOrWhiteSpace(targetType) && normalizedType is null)
            return new UserNoteSearchResult([], 0, false);

        var size = Math.Clamp(pageSize, 1, 200);
        var offset = Math.Max(0, pageOffset);
        var normalizedQuery = query.Trim();

        IQueryable<UserNoteEntity> notes = db.UserNotes
            .AsNoTracking()
            .Where(x => x.OwnerSubject == ownerSubject);

        if (normalizedType is not null)
            notes = notes.Where(x => x.TargetType == normalizedType);

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
            notes = ApplyNoteSearch(notes, normalizedQuery);

        var total = await notes.CountAsync(ct);
        var items = await notes
            .OrderByDescending(x => x.UpdatedAt)
            .Skip(offset)
            .Take(size)
            .ToListAsync(ct);

        return new UserNoteSearchResult(items, total, offset + items.Count < total);
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetVideoNotesAsync(
        string ownerSubject,
        IReadOnlyCollection<Guid> mediaGuids,
        CancellationToken ct = default)
        => GetGuidTargetNotesAsync(ownerSubject, UserNoteTargetTypes.Video, mediaGuids, ct);

    public async Task<IReadOnlyDictionary<long, string>> GetChannelNotesAsync(
        string ownerSubject,
        IReadOnlyCollection<long> accountIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject) || accountIds.Count == 0)
            return new Dictionary<long, string>();

        var ids = accountIds
            .Distinct()
            .Select(x => x.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var rows = await db.UserNotes
            .AsNoTracking()
            .Where(x => x.OwnerSubject == ownerSubject
                && x.TargetType == UserNoteTargetTypes.Channel
                && ids.Contains(x.TargetId))
            .ToListAsync(ct);

        return rows.ToDictionary(
            x => long.Parse(x.TargetId, CultureInfo.InvariantCulture),
            x => x.Note);
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetPlaylistNotesAsync(
        string ownerSubject,
        IReadOnlyCollection<Guid> playlistIds,
        CancellationToken ct = default)
        => GetGuidTargetNotesAsync(ownerSubject, UserNoteTargetTypes.Playlist, playlistIds, ct);

    public async Task<IReadOnlyList<Guid>> SearchVideoNoteTargetsAsync(
        string ownerSubject,
        string query,
        int limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject) || string.IsNullOrWhiteSpace(query))
            return [];

        var boundedLimit = Math.Clamp(limit, 1, 200);
        var rows = await ApplyNoteSearch(db.UserNotes.AsNoTracking().Where(x =>
                x.OwnerSubject == ownerSubject &&
                x.TargetType == UserNoteTargetTypes.Video), query.Trim())
            .OrderByDescending(x => x.UpdatedAt)
            .Take(boundedLimit)
            .Select(x => x.TargetId)
            .ToListAsync(ct);

        return rows
            .Select(x => Guid.TryParseExact(x, "N", out var guid) ? guid : (Guid?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<Guid, string>> GetGuidTargetNotesAsync(
        string ownerSubject,
        string targetType,
        IReadOnlyCollection<Guid> targetIds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject) || targetIds.Count == 0)
            return new Dictionary<Guid, string>();

        var ids = targetIds.Distinct().Select(x => x.ToString("N")).ToArray();
        var rows = await db.UserNotes
            .AsNoTracking()
            .Where(x => x.OwnerSubject == ownerSubject
                && x.TargetType == targetType
                && ids.Contains(x.TargetId))
            .ToListAsync(ct);

        return rows.ToDictionary(
            x => Guid.ParseExact(x.TargetId, "N"),
            x => x.Note);
    }

    private IQueryable<UserNoteEntity> ApplyNoteSearch(IQueryable<UserNoteEntity> query, string value)
    {
        if (IsInMemory())
        {
            var lower = value.ToLowerInvariant();
            return query.Where(x => x.Note.ToLower().Contains(lower));
        }

        return query.Where(x => EF.Functions.ILike(x.Note, "%" + EscapeLike(value) + "%"));
    }

    private async Task<TargetValidationResult> NormalizeAndValidateAsync(
        string ownerSubject,
        string targetType,
        string targetId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject))
            return TargetValidationResult.Fail("validation", "owner is required.");

        var normalized = NormalizeTarget(targetType, targetId);
        if (normalized is null)
            return TargetValidationResult.Fail("validation", "target type or target id is invalid.");

        var exists = await TargetExistsAsync(ownerSubject, normalized.Value.TargetType, normalized.Value.TargetId, ct);
        if (!exists)
            return TargetValidationResult.Fail("target_not_found", $"Target '{normalized.Value.TargetType}/{normalized.Value.TargetId}' was not found.");

        return TargetValidationResult.Ok(normalized.Value.TargetType, normalized.Value.TargetId);
    }

    private async Task<bool> TargetExistsAsync(string ownerSubject, string targetType, string targetId, CancellationToken ct)
    {
        return targetType switch
        {
            UserNoteTargetTypes.Video => await db.Media.AnyAsync(x => x.MediaGuid == Guid.ParseExact(targetId, "N"), ct),
            UserNoteTargetTypes.Playlist => await PlaylistExistsAsync(ownerSubject, Guid.ParseExact(targetId, "N"), ct),
            UserNoteTargetTypes.Channel => await ChannelExistsAsync(long.Parse(targetId, CultureInfo.InvariantCulture), ct),
            _ => false
        };
    }

    private async Task<bool> PlaylistExistsAsync(string ownerSubject, Guid playlistId, CancellationToken ct)
        => await db.Playlists.AnyAsync(x => x.PlaylistId == playlistId, ct)
            || await db.UserPlaylists.AnyAsync(x => x.OwnerSubject == ownerSubject && x.PlaylistId == playlistId, ct);

    private async Task<bool> ChannelExistsAsync(long accountId, CancellationToken ct)
    {
        if (IsInMemory())
            return true;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened)
            await conn.OpenAsync(ct);

        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM metadata.accounts WHERE id = @account_id)",
                conn);
            cmd.Parameters.AddWithValue("@account_id", accountId);
            return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
        }
        finally
        {
            if (opened)
                await conn.CloseAsync();
        }
    }

    private static (string TargetType, string TargetId)? NormalizeTarget(string targetType, string targetId)
    {
        var type = UserNoteTargetTypes.Normalize(targetType);
        if (type is null || string.IsNullOrWhiteSpace(targetId))
            return null;

        var trimmedId = targetId.Trim();
        return type switch
        {
            UserNoteTargetTypes.Video when Guid.TryParse(trimmedId, out var id) => (type, id.ToString("N")),
            UserNoteTargetTypes.Playlist when Guid.TryParse(trimmedId, out var id) => (type, id.ToString("N")),
            UserNoteTargetTypes.Channel when long.TryParse(trimmedId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                => (type, id.ToString(CultureInfo.InvariantCulture)),
            _ => null
        };
    }

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;

        var trimmed = note.Trim();
        return trimmed.Length > MaxNoteLength ? trimmed[..MaxNoteLength] : trimmed;
    }

    private static string EscapeLike(string value)
        => value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private bool IsInMemory()
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private sealed record TargetValidationResult(
        bool Success,
        string? TargetType = null,
        string? TargetId = null,
        string? ErrorCode = null,
        string? ErrorMessage = null)
    {
        public static TargetValidationResult Ok(string targetType, string targetId) => new(true, targetType, targetId);

        public static TargetValidationResult Fail(string code, string message) => new(false, ErrorCode: code, ErrorMessage: message);
    }
}
