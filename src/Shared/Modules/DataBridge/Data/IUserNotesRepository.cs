using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public interface IUserNotesRepository
{
    Task<UserNoteMutationResult> UpsertAsync(
        string ownerSubject,
        string targetType,
        string targetId,
        string note,
        CancellationToken ct = default);

    Task<UserNoteEntity?> GetAsync(
        string ownerSubject,
        string targetType,
        string targetId,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(
        string ownerSubject,
        string targetType,
        string targetId,
        CancellationToken ct = default);

    Task<UserNoteSearchResult> SearchAsync(
        string ownerSubject,
        string query,
        string? targetType,
        int pageSize,
        int pageOffset,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, string>> GetVideoNotesAsync(
        string ownerSubject,
        IReadOnlyCollection<Guid> mediaGuids,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<long, string>> GetChannelNotesAsync(
        string ownerSubject,
        IReadOnlyCollection<long> accountIds,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, string>> GetPlaylistNotesAsync(
        string ownerSubject,
        IReadOnlyCollection<Guid> playlistIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> SearchVideoNoteTargetsAsync(
        string ownerSubject,
        string query,
        int limit,
        CancellationToken ct = default);
}

public sealed record UserNoteMutationResult(
    bool Success,
    UserNoteEntity? Note = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static UserNoteMutationResult Ok(UserNoteEntity note) => new(true, note);

    public static UserNoteMutationResult Fail(string code, string message) => new(false, null, code, message);
}

public sealed record UserNoteSearchResult(
    IReadOnlyList<UserNoteEntity> Items,
    int TotalCount,
    bool HasMore);
