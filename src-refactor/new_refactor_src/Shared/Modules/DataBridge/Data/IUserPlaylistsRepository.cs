using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public interface IUserPlaylistsRepository
{
    Task<UserPlaylistDetail> CreateAsync(string ownerSubject, string name, string? description, CancellationToken ct = default);

    Task<UserPlaylistDetail?> UpdateAsync(string ownerSubject, Guid playlistId, string name, string? description, CancellationToken ct = default);

    Task<bool> DeleteAsync(string ownerSubject, Guid playlistId, CancellationToken ct = default);

    Task<UserPlaylistDetail?> GetAsync(string ownerSubject, Guid playlistId, CancellationToken ct = default);

    Task<IReadOnlyList<UserPlaylistSummary>> ListAsync(string ownerSubject, int pageSize, int pageOffset, CancellationToken ct = default);

    Task<UserPlaylistMutationResult> AddItemAsync(string ownerSubject, Guid playlistId, Guid mediaGuid, int? position, CancellationToken ct = default);

    Task<UserPlaylistMutationResult> RemoveItemAsync(string ownerSubject, Guid playlistId, Guid mediaGuid, CancellationToken ct = default);

    Task<UserPlaylistMutationResult> ReorderItemsAsync(string ownerSubject, Guid playlistId, IReadOnlyList<Guid> mediaGuids, CancellationToken ct = default);
}

public sealed record UserPlaylistSummary(UserPlaylistEntity Playlist, int ItemCount);

public sealed record UserPlaylistDetail(UserPlaylistEntity Playlist, IReadOnlyList<UserPlaylistItemEntity> Items);

public sealed record UserPlaylistMutationResult(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    UserPlaylistDetail? Detail)
{
    public static UserPlaylistMutationResult Ok(UserPlaylistDetail detail)
        => new(true, null, null, detail);

    public static UserPlaylistMutationResult Fail(string errorCode, string errorMessage)
        => new(false, errorCode, errorMessage, null);
}
