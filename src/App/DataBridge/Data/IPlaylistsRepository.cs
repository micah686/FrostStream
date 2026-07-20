using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

/// <summary>
/// Persistence façade for the playlist pipeline. Scoped — resolve through
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/> from singletons.
/// </summary>
public interface IPlaylistsRepository
{
    Task<PlaylistEntity?> GetByIdAsync(Guid playlistId, CancellationToken ct = default);

    Task<PlaylistEntity?> FindBySourceUrlAsync(string sourceUrl, CancellationToken ct = default);

    /// <summary>
    /// Inserts the playlist row when missing. If a row already exists for the same id (or for
    /// the same source URL — see <see cref="UpsertResult.WasReused"/>), the existing row is
    /// returned and re-driven through metadata fetch rather than duplicating it.
    /// </summary>
    Task<UpsertResult> CreateOrReuseAsync(PlaylistRequested request, CancellationToken ct = default);

    Task UpdateStateAsync(Guid playlistId, PlaylistState state, CancellationToken ct = default);

    Task ApplyMetadataFetchedAsync(Guid playlistId, PlaylistMetadataFetched evt, CancellationToken ct = default);

    /// <summary>Inserts the staging rows (idempotent — duplicates are skipped).</summary>
    Task WriteStagingEntriesAsync(Guid playlistId, IReadOnlyList<PlaylistEntry> entries, CancellationToken ct = default);

    /// <summary>Loads the staging rows for a playlist, oldest-first.</summary>
    Task<IReadOnlyList<PlaylistScanEntryEntity>> ListStagingEntriesAsync(Guid playlistId, CancellationToken ct = default);

    /// <summary>
    /// Persists the per-entry fan-out atomically: inserts the <c>download_jobs</c> row,
    /// inserts the matching <c>playlist_items</c> row, and removes the staging row.
    /// Skips when a <c>playlist_items</c> row already exists for the slot.
    /// </summary>
    Task FanOutEntryAsync(FanOutEntryRequest request, CancellationToken ct = default);

    Task<bool> TryLinkMediaGuidAsync(Guid jobId, Guid mediaGuid, CancellationToken ct = default);

    /// <summary>
    /// Clears the ignored state of a playlist entry's job (state back to
    /// <see cref="DownloadJobState.Queued"/>, keyword cleared) and returns its entry URL so the caller
    /// can publish a forced download. Returns null when the job is not part of the playlist.
    /// </summary>
    Task<string?> RequeuePlaylistItemAsync(Guid playlistId, Guid jobId, CancellationToken ct = default);

    Task<PlaylistAudioPreference?> GetAudioPreferenceForJobAsync(Guid jobId, CancellationToken ct = default);

    Task<IReadOnlyList<PlaylistSummary>> ListAsync(int pageSize, int pageOffset, CancellationToken ct = default);

    Task<PlaylistDetail?> GetDetailAsync(Guid playlistId, CancellationToken ct = default);
}

public sealed record UpsertResult(PlaylistEntity Playlist, bool WasReused);

public sealed record FanOutEntryRequest(
    Guid PlaylistId,
    Guid CorrelationId,
    Guid JobId,
    int PlaylistIndex,
    string EntryUrl,
    string? EntryTitle,
    string? RequestedBy,
    string? StorageKey,
    DownloadJobState InitialState = DownloadJobState.Queued,
    string? IgnoredKeyword = null);

public sealed record PlaylistSummary(
    PlaylistEntity Playlist,
    int CompletedItems,
    int FailedItems,
    int PendingItems);

public sealed record PlaylistDetail(
    PlaylistEntity Playlist,
    int CompletedItems,
    int FailedItems,
    int PendingItems,
    IReadOnlyList<PlaylistDetailItem> Items);

public sealed record PlaylistDetailItem(
    int PlaylistIndex,
    Guid JobId,
    string EntryUrl,
    string? EntryTitle,
    DownloadJobStatus JobStatus,
    Guid? MediaGuid,
    string? IgnoredKeyword);

public sealed record PlaylistAudioPreference(bool EncodeForPlaylist, string? StorageKey);
